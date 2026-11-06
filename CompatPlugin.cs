// Compatibility plugin for CSpect: it fixes some incompatibilities between CSpect and games for the Next.
// The fixes are chosen per game with the CSPECT_COMPAT environment variable (comma-separated list). Without it, they come from Compat.ini next
// to Compat.dll: the options of the file CSpect has loaded, found by its MD5, or the default= options.
//
//   l2bank9   Layer 2 starts at 16K bank 9 (Next register $12). CSpect sets it to 8 when it loads a .snx;
//             games that never set it and expect 9 (e.g. Montana Mike) then draw over their data.
//   nodivmmc  DivMMC automap off (bit 4 of Next register $0A). CSpect maps the DivMMC memory when the
//             CPU reaches $0038 even with RAM in place of the ROM; a game that calls code there (e.g. Deltas
//             Shadow) then runs the DivMMC memory, which CSpect fills with random values.
//   dd01nop   DD 01 in the game's .nex is a 2-byte NOP. These bytes are breakpoints left in a game by its
//             developer (e.g. Elite Next); CSpect runs them as LD BC,nn, which swallows the next instruction. The .nex
//             is the one in CSPECT_COMPAT_FILE, or else the one CSpect has loaded; the bytes are changed in
//             memory only, before the game starts.
//   zeroram   RAM outside the 128K snapshot (8K pages 16-255) cleared before the game starts.
//             CSpect fills it with random values, and a game that shows Layer 2 without clearing it
//             (e.g. Spice Invaderz) shows them. Only for .snx/.sna games: a .nex can load banks there.
//   divfirst  DivMMC memory comes before Layer 2 mapping, as on the Next. With Layer 2 write-mapped at $0000
//             (port $123B bit 0), CSpect sends the writes of DivMMC code (NextZXOS's interrupt handler and
//             tape traps) to Layer 2, so NextZXOS loses its saved stack pointer and the machine resets (e.g.
//             4K Race Next, whose loader copies its screen into Layer 2 while loading from tape). While DivMMC
//             code runs, those writes are kept by the plugin and given back when it reads them.
//   im2ff     IM 2 vector read at I*256+$FF, as on the Next (the bus reads $FF). CSpect reads it at I*256+$00,
//             so a game that sets only the vector at $xxFF (e.g. 4K Race Next, Manic Miner Trick Or Treat AY)
//             jumps to a random address. CSpect calls the plugins' Tick right after it has taken the frame
//             interrupt: if the CPU has just jumped through the vector at $xx00, it is sent through $xxFF.
//   sdstream  streaming from the SD card done by the plugin. NextZXOS's DISK_STRMSTART can fail in CSpect, and a
//             game that streams its music from the SD card (e.g. The Way of the Exploding Fist) stops on a
//             black screen. RST $08 DISK_STRMSTART / DISK_STRMEND return at once without error; while the
//             stream is on, the game's own reads of the SD card port $EB get the file's blocks, each followed
//             by 2 CRC bytes and the data token of the next one. The file is the one the game mapped with
//             DISK_FILEMAP, read from the game's folder on the PC (silence if it is not there).
//   divnmi    DivMMC NMI (Next register $02 bit 2) with the DivMMC memory paged in, as on the Next: from $0066
//             to the RETN, $0000-$1FFF is the DivMMC ROM and $2000-$3FFF the DivMMC RAM bank of port $E3, as the
//             game wrote them (the RAM with CONMEM, port $E3 bit 7; the ROM in config mode, registers $03 and
//             $04); a new request while it is paged in is ignored. CSpect runs the game's RAM at $0066, so a
//             game that plays its sound from DivMMC NMIs (e.g. Atic Atac Next) crashes, or stays on the title
//             screen waiting for the sound. The plugin keeps its own copy of that memory.
//             If the game also lets NMIs break the DMA (Next register $CC bit 7), which CSpect ignores, long DMA
//             transfers from memory to memory or to the sprite ports are done at once by the plugin: otherwise
//             the CPU stops during them, the NMIs are lost and the sound crackles (e.g. Atic Atac Next).
//   notemu    Next register $00 (machine ID) reads as a Next instead of an emulator, for the game's code only
//             (code at $4000 and above; NextZXOS still sees CSpect). A game that treats emulators differently
//             (e.g. the 2023 version of The Way of the Exploding Fist, which then plays its music with a slower
//             timer) behaves as on the Next.
//   pal44     a palette colour written through Next register $44 changes only at the second of its two writes, as
//             on the Next. CSpect changes it at the first, so a game that writes $44 once and then moves on (e.g.
//             The Way of the Exploding Fist, which turns colour 128 of its title black) gets a wrong colour.
//   ctcirq    CTC interrupts turned off (Next register $C5) and on again work as on the Next. In CSpect they do not
//             come back without programming the CTC again, so a game that only turns them on again (e.g. the 2024
//             version of The Way of the Exploding Fist, for the fanfare of its SpecNext screen) stays silent. When
//             a channel comes back without having been programmed, the plugin has the CPU program it again, with
//             the game's last values, in a few instructions of its own; the game's registers are then restored.
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using Plugin;

namespace Compat
{
    public class CompatPlugin : iPlugin
    {
        iCSpect CSpect;
        bool l2bank9, nodivmmc, dd01nop, zeroram, divfirst, im2ff, sdstream, divnmi, notemu, pal44, ctcirq, cleared;
        long frame;
        List<int> breakpoints;         // DD 01 positions: 16K bank * 16384 + offset
        int patched;

        // divfirst: DivMMC entry points (automap on the opcode fetch) and the exit range $1FF8-$1FFF
        static readonly int[] DivEntries = { 0x0000, 0x0008, 0x0038, 0x0066, 0x04C6, 0x0562 };
        bool divActive, busy;
        int layer2Port, divBank;       // last values written to ports $123B and $E3
        Dictionary<int, byte> divRam = new Dictionary<int, byte>();   // DivMMC RAM written under Layer 2: bank * 8192 + offset

        // Compat.ini: MD5 of a file (lower case hex) -> options; "default" -> options for the other files
        Dictionary<string, string> byMd5 = new Dictionary<string, string>();
        bool fromIni;                  // no CSPECT_COMPAT: the options are chosen when CSpect loads a file
        string loadedMd5;              // MD5 of the file already identified (Reset comes again after F3)

        // sets the option flags from a comma-separated list
        void SetOptions(string opts)
        {
            l2bank9 = nodivmmc = dd01nop = zeroram = divfirst = im2ff = sdstream = divnmi = notemu = pal44 = ctcirq = false;
            foreach (string o in opts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (o.Equals("l2bank9", StringComparison.OrdinalIgnoreCase)) l2bank9 = true;
                else if (o.Equals("nodivmmc", StringComparison.OrdinalIgnoreCase)) nodivmmc = true;
                else if (o.Equals("dd01nop", StringComparison.OrdinalIgnoreCase)) dd01nop = true;
                else if (o.Equals("zeroram", StringComparison.OrdinalIgnoreCase)) zeroram = true;
                else if (o.Equals("divfirst", StringComparison.OrdinalIgnoreCase)) divfirst = true;
                else if (o.Equals("im2ff", StringComparison.OrdinalIgnoreCase)) im2ff = true;
                else if (o.Equals("sdstream", StringComparison.OrdinalIgnoreCase)) sdstream = true;
                else if (o.Equals("divnmi", StringComparison.OrdinalIgnoreCase)) divnmi = true;
                else if (o.Equals("notemu", StringComparison.OrdinalIgnoreCase)) notemu = true;
                else if (o.Equals("pal44", StringComparison.OrdinalIgnoreCase)) pal44 = true;
                else if (o.Equals("ctcirq", StringComparison.OrdinalIgnoreCase)) ctcirq = true;
                else Console.WriteLine(" Compat: unknown option " + o);
            }
        }

        void ReadIni()
        {
            // next to Compat.dll; CSpect may load plugins without a location: then next to CSpect.exe
            string dir = typeof(CompatPlugin).Assembly.Location;
            dir = string.IsNullOrEmpty(dir) ? AppDomain.CurrentDomain.BaseDirectory : Path.GetDirectoryName(dir);
            string ini = Path.Combine(dir, "Compat.ini");
            if (!File.Exists(ini)) return;
            foreach (string raw in File.ReadAllLines(ini))
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#' || line[0] == '[') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                byMd5[line.Substring(0, eq).Trim().ToLowerInvariant()] = line.Substring(eq + 1).Trim();
            }
        }

        // the file CSpect has loaded (its name as given on the command line; at Reset the current directory
        // is the file's folder)
        string LoadedFile()
        {
            string f = CSpect.GetGlobal(eGlobal.file_name) as string;
            if (string.IsNullOrEmpty(f)) return null;
            try { f = Path.GetFullPath(f); } catch { return null; }
            return File.Exists(f) ? f : null;
        }

        static string Md5(string file)
        {
            using (MD5 md5 = MD5.Create())
            using (FileStream s = File.OpenRead(file))
                return BitConverter.ToString(md5.ComputeHash(s)).Replace("-", "").ToLowerInvariant();
        }

        // Compat.ini: the options of the loaded file (found by its MD5), or the default ones. Only .nex, .snx
        // and .sna files are identified: not an SD card image, with a game started from NextZXOS
        void Identify()
        {
            string file = LoadedFile();
            string ext = file == null ? "" : Path.GetExtension(file).ToLowerInvariant();
            if (ext != ".nex" && ext != ".snx" && ext != ".sna") file = null;
            string md5 = file == null ? "" : Md5(file);
            if (md5 == loadedMd5) return;
            loadedMd5 = md5;
            cleared = false; patched = 0; breakpoints = null;
            string opts;
            if (md5 == "" || !byMd5.TryGetValue(md5, out opts)) byMd5.TryGetValue("default", out opts);
            SetOptions(opts ?? "");
            if (file != null) Console.WriteLine(" Compat: " + Path.GetFileName(file) + " MD5 " + md5 + (string.IsNullOrEmpty(opts) ? "" : " -> " + opts));
            if (dd01nop) breakpoints = FindBreakpoints(file);
        }

        public List<sIO> Init(iCSpect _CSpect)
        {
            CSpect = _CSpect;
            List<sIO> io = new List<sIO>();
            string opts = Environment.GetEnvironmentVariable("CSPECT_COMPAT");
            if (opts == null)
            {
                // the hooks of every option in Compat.ini; the options themselves are set when a file loads
                fromIni = true;
                ReadIni();
                SetOptions(string.Join(",", new List<string>(byMd5.Values).ToArray()));
            }
            else
            {
                SetOptions(opts);
                // without CSPECT_COMPAT_FILE: the file CSpect loads, found at Reset
                string file = Environment.GetEnvironmentVariable("CSPECT_COMPAT_FILE");
                if (dd01nop && !string.IsNullOrEmpty(file)) breakpoints = FindBreakpoints(file);
            }
            if (pal44 && !dd01nop && !zeroram) foreach (int r in new[] { 0x40, 0x41, 0x43, 0x44 }) io.Add(new sIO(r, eAccess.NextReg_Write, 0, 0));
            if (ctcirq)
            {
                if (!dd01nop && !zeroram) io.Add(new sIO(0xC5, eAccess.NextReg_Write, 0, 0));
                for (int c = 0x18; c <= 0x1F; c++) io.Add(new sIO((c << 8) | 0x3B, eAccess.Port_Write, 0, 0));
                for (int a = CtcStub; a < CtcStub + CtcStubCode.Length; a++) io.Add(new sIO(a, eAccess.Memory_Read, 0, 0));
                if (!divnmi) for (int a = 0; a < 0x10000; a++) io.Add(new sIO(a, eAccess.Memory_EXE, 0, 0));
            }
            if (notemu)
            {
                io.Add(new sIO(0x243B, eAccess.Port_Write, 0, 0));
                io.Add(new sIO(0x253B, eAccess.Port_Read, 0, 0));
            }
            if (dd01nop || zeroram)
            {
                // CSpect writes the Next registers while it loads the file: the patch and the RAM clear are
                // done at those writes (frame 0), so that they are in place before the game's first instruction
                for (int r = 0; r < 256; r++) io.Add(new sIO(r, eAccess.NextReg_Write, 0, 0));
            }
            else if (divnmi)
            {
                for (int r = 2; r <= 4; r++) io.Add(new sIO(r, eAccess.NextReg_Write, 0, 0));
                io.Add(new sIO(0xCC, eAccess.NextReg_Write, 0, 0));
            }
            if (divnmi)
            {
                // OUT (C),r puts B on the high byte of the port: every $xx6B
                for (int b = 0; b < 256; b++) io.Add(new sIO((b << 8) | 0x6B, eAccess.Port_Write, 0, 0));
                // OUT ($E3),A puts A on the high byte of the port: every $xxE3
                for (int b = 0; b < 256; b++) if (b != 0 || !divfirst) io.Add(new sIO((b << 8) | 0xE3, eAccess.Port_Write, 0, 0));
                for (int a = 0; a < 0x4000; a++)
                {
                    if (divfirst && a >= 0x2000) continue;
                    io.Add(new sIO(a, eAccess.Memory_Write, 0, 0));
                    io.Add(new sIO(a, eAccess.Memory_Read, 0, 0));
                }
                // the RETN can return anywhere: the end of the NMI is seen at the next instruction
                for (int a = 0; a < 0x10000; a++) io.Add(new sIO(a, eAccess.Memory_EXE, 0, 0));
            }
            if (divfirst)
            {
                io.Add(new sIO(0x123B, eAccess.Port_Write, 0, 0));
                io.Add(new sIO(0x00E3, eAccess.Port_Write, 0, 0));
                foreach (int a in DivEntries) io.Add(new sIO(a, eAccess.Memory_EXE, 0, 0));
                for (int a = 0x3D00; a <= 0x3DFF; a++) io.Add(new sIO(a, eAccess.Memory_EXE, 0, 0));
                for (int a = 0x1FF8; a <= 0x1FFF; a++) io.Add(new sIO(a, eAccess.Memory_EXE, 0, 0));
                for (int a = 0x2000; a < 0x4000; a++)
                {
                    io.Add(new sIO(a, eAccess.Memory_Write, 0, 0));
                    io.Add(new sIO(a, eAccess.Memory_Read, 0, 0));
                }
            }
            if (sdstream)
            {
                if (!divfirst) io.Add(new sIO(0x0008, eAccess.Memory_EXE, 0, 0));
                // the SD card port with any B: INI changes B at each byte, and CSpect matches all 16 bits
                for (int b = 0; b < 256; b++) io.Add(new sIO((b << 8) | 0xEB, eAccess.Port_Read, 0, 0));
            }
            if (fromIni)
            {
                if (byMd5.Count > 0) Console.WriteLine(" Compat added (Compat.ini, " + byMd5.Count + " entries)");
                SetOptions("");
            }
            else if (l2bank9 || nodivmmc || dd01nop || zeroram || divfirst || im2ff || sdstream || divnmi || notemu || pal44 || ctcirq) Console.WriteLine(" Compat added (" + opts.Trim() + ")");
            return io;
        }

        // DD 01 in the banks of a .nex file (header 512 bytes, then the loading screens, then the banks
        // present in the order 5, 2, 0, 1, 3, 4, 6, 7 ... 111)
        static List<int> FindBreakpoints(string file)
        {
            List<int> found = new List<int>();
            if (string.IsNullOrEmpty(file) || !File.Exists(file) || !file.EndsWith(".nex", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(" Compat: dd01nop needs a .nex (CSPECT_COMPAT_FILE or the file loaded by CSpect)");
                return found;
            }
            byte[] b = File.ReadAllBytes(file);
            if (b.Length < 512 || b[0] != 'N' || b[1] != 'e' || b[2] != 'x' || b[3] != 't') return found;
            int screens = b[10], pos = 512;
            bool palette = (screens & 0x80) == 0;
            if ((screens & 0x01) != 0) pos += (palette ? 512 : 0) + 49152;      // Layer 2 256x192
            if ((screens & 0x02) != 0) pos += 6912;                               // ULA
            if ((screens & 0x04) != 0) pos += (palette ? 512 : 0) + 12288;      // LoRes
            if ((screens & 0x08) != 0) pos += 12288;                              // Timex HiRes
            if ((screens & 0x10) != 0) pos += 12288;                              // Timex HiColour
            if ((screens & 0x40) != 0) pos += (palette ? 512 : 0) + 81920;      // Layer 2 320x256 / 640x256
            List<int> order = new List<int> { 5, 2, 0, 1, 3, 4 };
            for (int k = 6; k < 112; k++) order.Add(k);
            foreach (int bank in order)
            {
                if (b[18 + bank] == 0) continue;
                if (pos + 16384 > b.Length) break;
                for (int i = 0; i < 16383; i++)
                    if (b[pos + i] == 0xDD && b[pos + i + 1] == 0x01) found.Add(bank * 16384 + i);
                pos += 16384;
            }
            return found;
        }

        // bank 5 (pages 10-11) is kept apart by CSpect: it is written at $4000, where it is when a file loads
        byte PeekBank(int bank, int offset)
        {
            if (bank == 5) return CSpect.Peek((ushort)(0x4000 + offset));
            return CSpect.PeekPhysical(bank * 16384 + offset);
        }

        void PokeBank(int bank, int offset, byte v)
        {
            if (bank == 5) CSpect.Poke((ushort)(0x4000 + offset), v);
            else CSpect.PokePhysical(bank * 16384 + offset, v);
        }

        void PatchBreakpoints()
        {
            if (breakpoints == null) return;
            foreach (int p in breakpoints)
            {
                int bank = p / 16384, off = p % 16384;
                if (PeekBank(bank, off) == 0xDD && PeekBank(bank, off + 1) == 0x01)
                {
                    PokeBank(bank, off, 0); PokeBank(bank, off + 1, 0);
                    patched++;
                }
            }
        }

        bool Layer2WriteMapped() { return (layer2Port & 1) != 0; }      // always over $0000-$3FFF

        public bool Write(eAccess _type, int _port, int _id, byte _value)
        {
            if (_type == eAccess.Port_Write)
            {
                if (_port == 0x123B) layer2Port = _value;
                else if (_port == 0x243B) nextRegSelected = _value;
                else if (ctcirq && (_port & 0xFF) == 0x3B && (_port >> 8) >= 0x18 && (_port >> 8) <= 0x1F) CtcWrite((_port >> 8) - 0x18, _value);
                else if ((_port & 0xFF) == 0xE3) { divBank = _value & 0x0F; divCtl = _value; }
                else if (divnmi && (_port & 0xFF) == 0x6B) return DmaWrite(_value);
                return false;
            }
            if (_type == eAccess.Memory_Write)
            {
                if (busy) return false;
                // in config mode, SRAM bank 4 ($10000) is the DivMMC ROM: the game writes its NMI entry there
                if (divnmi && _port < 0x2000 && configMode && configBank == 4) { divRom[_port] = _value; return true; }
                if (divnmi && _port < 0x4000 && DivPaged())
                {
                    if (_port >= 0x2000) divBanks[divBank * 8192 + _port - 0x2000] = _value;
                    else if (!nmiActive) divRom[_port] = _value;      // ROM: written only with CONMEM
                    return true;
                }
                if (divfirst && divActive && _port >= 0x2000 && _port < 0x4000)
                {
                    int key = divBank * 8192 + (_port - 0x2000);
                    if (Layer2WriteMapped())
                    {
                        divRam[key] = _value;
                        return true;           // handled: not into Layer 2
                    }
                    divRam.Remove(key);        // written to the DivMMC RAM by CSpect: no longer kept here
                }
                return false;
            }
            if (_type == eAccess.NextReg_Write && ctcirq && _port == 0xC5) CtcEnable(_value);
            if (_type == eAccess.NextReg_Write && pal44 && !pal44Busy)
            {
                if (_port == 0x40 || _port == 0x41 || _port == 0x43) pal44First = -1;   // back to the first write
                else if (_port == 0x44)
                {
                    if (pal44First < 0) { pal44First = _value; return true; }           // kept until the second write
                    pal44Busy = true;
                    CSpect.SetNextRegister(0x44, (byte)pal44First, 0);
                    pal44Busy = false;
                    pal44First = -1;
                    return false;
                }
            }
            // divnmi: an NMI is requested (written 0 to clear the request)
            // (ignored while the DivMMC memory is paged in, as on the Next: the request is dropped)
            if (_type == eAccess.NextReg_Write && divnmi && _port == 0x02)
            {
                if (nmiActive && (_value & 0x04) != 0) return true;
                nmiPending = (_value & 0x04) != 0;
            }
            // divnmi: config mode (machine type 7 in register $03), with register $04 = SRAM bank at $0000
            if (_type == eAccess.NextReg_Write && divnmi && _port == 0x03) configMode = (_value & 0x07) == 0x07;
            if (_type == eAccess.NextReg_Write && divnmi && _port == 0x04) configBank = _value;
            if (_type == eAccess.NextReg_Write && divnmi && _port == 0xCC) nmiBreaksDma = (_value & 0x80) != 0;
            // the loader's first register write comes after CSpect has filled the RAM with random values
            if (_type == eAccess.NextReg_Write && frame == 0 && zeroram && !cleared)
            {
                cleared = true;
                for (int i = 16 * 8192; i < 256 * 8192; i++) CSpect.PokePhysical(i, 0);
            }
            if (_type == eAccess.NextReg_Write && frame == 0 && patched < (breakpoints == null ? 0 : breakpoints.Count)) PatchBreakpoints();
            return false;
        }

        public void Tick()
        {
            if (im2ff) RedirectIm2();
            // first frame: after CSpect has loaded the file and set its registers
            if (++frame != 1) return;
            if (dd01nop && breakpoints != null)
            {
                PatchBreakpoints();
                Console.WriteLine(" Compat: " + patched + " of " + breakpoints.Count + " DD 01 breakpoints turned into NOPs");
            }
            if (l2bank9) CSpect.SetNextRegister(0x12, 9, 0);
            if (nodivmmc) CSpect.SetNextRegister(0x0A, (byte)(CSpect.GetNextRegister(0x0A, 0) & ~0x10), 0);
        }

        // im2ff: just after the frame interrupt (IFF1 off), PC = the vector at $xx00: use the one at $xxFF
        void RedirectIm2()
        {
            Z80Regs r = CSpect.GetRegs();
            if (r.IM != 2 || r.IFF1) return;
            ushort vec = (ushort)(r.I << 8);
            busy = true;
            int wrong = CSpect.Peek(vec) | (CSpect.Peek((ushort)(vec + 1)) << 8);
            int right = CSpect.Peek((ushort)(vec + 0xFF)) | (CSpect.Peek((ushort)(vec + 0x100)) << 8);
            busy = false;
            if (r.PC != wrong || wrong == right) return;
            r.PC = (ushort)right;
            CSpect.SetRegs(r);
        }

        // sdstream: RST $08 with DISK_STRMSTART ($86) or DISK_STRMEND ($87) after it returns at once with
        // carry clear (no error), as if the stream had started or ended. F_OPEN ($9A) and DISK_FILEMAP ($85)
        // are only watched, to know which file is streamed.
        bool SkipStream()
        {
            Z80Regs r = CSpect.GetRegs();
            busy = true;
            int ret = CSpect.Peek(r.SP) | (CSpect.Peek((ushort)(r.SP + 1)) << 8);
            byte fn = CSpect.Peek((ushort)ret);
            if (fn == 0x9A)
            {
                string name = "";
                for (int k = 0; k < 255; k++)
                {
                    byte ch = CSpect.Peek((ushort)(r.IX + k));
                    if (ch == 0) break;
                    name += (char)ch;
                }
                lastOpened = name;
            }
            busy = false;
            if (fn == 0x85) StreamFile(lastOpened);
            if (fn != 0x86 && fn != 0x87) return false;
            if (fn == 0x86) StreamStart(((long)r.IX << 16) | r.DE, r.BC);
            r.PC = (ushort)(ret + 1);
            r.SP = (ushort)(r.SP + 2);
            r.AF = (ushort)(r.AF & 0xFFFE);
            CSpect.SetRegs(r);
            streaming = fn == 0x86;
            return true;
        }

        // the stream's data: the file the game mapped (DISK_FILEMAP), read from the game's folder on the PC
        string lastOpened;
        FileStream music;
        string musicName;
        Dictionary<long, long> musicOffsets = new Dictionary<long, long>();   // card address -> offset in the file
        long musicEnd, musicPos;
        int streamCount;               // bytes of the block given (0-511), then the 2 CRC bytes (512-513)
        bool waitToken;                // after the CRC: $FF until the data token
        byte[] block = new byte[512];
        bool streaming;                // between the skipped DISK_STRMSTART and DISK_STRMEND

        void StreamFile(string name)
        {
            if (string.IsNullOrEmpty(name) || name == musicName) return;
            if (music != null) { music.Close(); music = null; }
            musicName = name; musicOffsets.Clear(); musicEnd = 0;
            string file = FindGameFile(name);
            if (file != null) music = File.OpenRead(file);
            Console.WriteLine(" Compat: stream " + name + (music != null ? "" : " (not found: silent)"));
        }

        // a file of the game, by its NextZXOS path relative to the game's folder (case is ignored, as on FAT)
        static string FindGameFile(string name)
        {
            string gf = Environment.GetEnvironmentVariable("CSPECT_COMPAT_FILE");
            string dir = string.IsNullOrEmpty(gf) ? Environment.CurrentDirectory : Path.GetDirectoryName(Path.GetFullPath(gf));
            foreach (string part in name.Replace('\\', '/').Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (part == ".") continue;
                string next = null;
                if (Directory.Exists(dir))
                    foreach (string e in Directory.GetFileSystemEntries(dir))
                        if (string.Equals(Path.GetFileName(e), part, StringComparison.OrdinalIgnoreCase)) { next = e; break; }
                if (next == null) return null;
                dir = next;
            }
            return File.Exists(dir) ? dir : null;
        }

        // the map's pieces are streamed in order: a card address not seen yet follows the previous ones, one
        // seen already (the music starting again) goes back to its offset
        void StreamStart(long address, int blocks)
        {
            long off;
            if (!musicOffsets.TryGetValue(address, out off))
            {
                off = musicEnd;
                musicOffsets[address] = off;
                musicEnd += (long)blocks * 512;
            }
            musicPos = off;
            streamCount = 0; waitToken = false;   // NextZXOS has already read the first block's data token
            ReadBlock();
        }

        // the next 512 bytes of the file (silence, $80, past its end or without a file)
        void ReadBlock()
        {
            int n = 0;
            if (music != null)
            {
                music.Position = musicPos;
                n = music.Read(block, 0, 512);
            }
            for (int i = n; i < 512; i++) block[i] = 0x80;
            musicPos += 512;
        }

        byte StreamByte()
        {
            if (waitToken)
            {
                waitToken = false;
                streamCount = 0;
                ReadBlock();
                return 0xFE;
            }
            if (streamCount < 512) return block[streamCount++];
            if (++streamCount == 514) waitToken = true;
            return 0xFF;                       // CRC
        }

        public byte Read(eAccess _type, int _port, int _id, out bool _isvalid)
        {
            _isvalid = false;
            if (_type == eAccess.Port_Read && _port == 0x253B)
            {
                if (nextRegSelected != 0 || CSpect.GetRegs().PC < 0x4000) return 0;
                _isvalid = true;
                return 0x0A;           // machine ID of the Next (CSpect: 8, emulator)
            }
            // sdstream: the game reads the stream itself from the SD card port: 512 bytes of each block, 2 CRC
            // bytes, then $FF until the data token $FE of the next block. NextZXOS's own reads ($0000-$3FFF) are
            // not touched.
            if (_type == eAccess.Port_Read)
            {
                if (!streaming || CSpect.GetRegs().PC < 0x4000) return 0;
                _isvalid = true;
                return StreamByte();
            }
            if (_type == eAccess.Memory_Read && ctcStubActive && _port >= CtcStub && _port < CtcStub + CtcStubCode.Length)
            {
                _isvalid = true;
                return CtcStubCode[_port - CtcStub];
            }
            if (_type == eAccess.Memory_Read)
            {
                if (divnmi && _port < 0x4000 && DivPaged()) { _isvalid = true; return DivPeek(_port); }
                byte v;
                if (divActive && divRam.Count > 0 && divRam.TryGetValue(divBank * 8192 + (_port - 0x2000), out v)) { _isvalid = true; return v; }
                return 0;
            }
            if (_type != eAccess.Memory_EXE) return 0;
            if (ctcirq) CtcExe(_port);
            if (divnmi) DivNmiExe(_port);
            if (sdstream && _port == 0x0008 && SkipStream()) return 0;
            if (!divfirst) return 0;
            // divfirst: DivMMC code runs from an entry point (with the ROM at $0000 and automap on) to $1FF8-$1FFF
            bool exit = _port >= 0x1FF8 && _port <= 0x1FFF;
            if (!divActive && !exit)
            {
                if (CSpect.GetNextRegister(0x50, 0) == 0xFF && (CSpect.GetNextRegister(0x0A, 0) & 0x10) != 0) divActive = true;
            }
            else if (divActive && exit) divActive = false;
            return 0;
        }

        // divnmi: the DivMMC memory as the game wrote it, and whether it is paged in by an NMI
        byte[] divRom = new byte[8192], divBanks = new byte[16 * 8192];
        int divCtl;                    // last value written to port $E3
        bool nmiPending, nmiActive, retnSeen, configMode;
        int configBank;

        bool DivPaged() { return nmiActive || (divCtl & 0x80) != 0; }
        byte DivPeek(int a) { return a < 0x2000 ? divRom[a] : divBanks[divBank * 8192 + a - 0x2000]; }

        // the NMI starts at $0066 after a request with register $02 and ends after the RETN
        void DivNmiExe(int pc)
        {
            if (nmiActive && retnSeen) { nmiActive = retnSeen = false; }
            if (!nmiActive && pc == 0x0066 && nmiPending) { nmiActive = true; nmiPending = false; }
            if (nmiActive && pc < 0x3FFF && DivPeek(pc) == 0xED && DivPeek(pc + 1) == 0x45) retnSeen = true;
        }

        // CSpect resets after loading a file, before the loader's register writes; the RAM is cleared once only,
        // not again after F3
        public void Reset()
        {
            nmiBreaksDma = false; dmaNext.Clear(); pal44First = -1; ctcEnabled = ctcStale = ctcRestart = 0; ctcStubActive = false;
            frame = 0; divActive = false; streaming = false; nmiPending = nmiActive = retnSeen = configMode = false; divCtl = configBank = 0;
            if (fromIni && byMd5.Count > 0) Identify();
            else if (dd01nop && breakpoints == null) breakpoints = FindBreakpoints(LoadedFile());
        }
        // ctcirq: the last values written to each CTC channel, and the channels turned off and not programmed since
        const int CtcStub = 0xFFF0;    // where the plugin's instructions are read from while they run
        static readonly byte[] CtcStubCode = { 0xED, 0x61, 0xED, 0x69, 0x00 };   // OUT (C),H; OUT (C),L; NOP
        int[] ctcControl = new int[8], ctcConstant = new int[8];
        bool[] ctcConstantNext = new bool[8];
        int ctcEnabled, ctcStale, ctcRestart;
        bool ctcStubActive;
        Z80Regs ctcSaved;

        void CtcEnable(byte v)
        {
            ctcStale |= ctcEnabled & ~v;               // turned off
            ctcRestart |= ctcStale & v & ~ctcEnabled;  // on again, not programmed
            ctcStale &= ~ctcRestart;
            ctcEnabled = v;
        }

        void CtcWrite(int channel, byte v)
        {
            if (ctcConstantNext[channel]) { ctcConstant[channel] = v; ctcConstantNext[channel] = false; }
            else if ((v & 1) != 0) { ctcControl[channel] = v; ctcConstantNext[channel] = (v & 4) != 0; }
            if (!ctcStubActive) { ctcStale &= ~(1 << channel); ctcRestart &= ~(1 << channel); }
        }

        void CtcExe(int pc)
        {
            if (ctcStubActive)
            {
                if (pc != CtcStub + 4) return;         // the NOP: done
                ctcStubActive = false;
                CSpect.SetRegs(ctcSaved);
                return;
            }
            if (ctcRestart == 0) return;
            int c = 0;
            while ((ctcRestart & (1 << c)) == 0) c++;
            ctcRestart &= ~(1 << c);
            if ((ctcControl[c] & 4) == 0) return;      // no time constant known
            ctcSaved = CSpect.GetRegs();
            Z80Regs r = CSpect.GetRegs();
            r.BC = (ushort)(((0x18 + c) << 8) | 0x3B);
            r.HL = (ushort)((ctcControl[c] << 8) | ctcConstant[c]);
            r.PC = CtcStub;
            r.IFF1 = false;
            ctcStubActive = true;
            CSpect.SetRegs(r);
        }

        int nextRegSelected = -1;      // notemu: last value written to port $243B
        int pal44First = -1;           // pal44: first write to register $44, waiting for the second
        bool pal44Busy;

        // divnmi: the zxnDMA registers, followed from the writes to port $6B
        const int DmaAtOnce = 256;     // shorter transfers stop the CPU for less than the time between two NMIs
        bool nmiBreaksDma, dmaAtoB = true, dmaAIo, dmaBIo, dmaTimed;
        int dmaAMode = 1, dmaBMode = 1, dmaA, dmaB, dmaLength;   // modes: 0 decrement, 1 increment, 2 fixed
        Queue<int> dmaNext = new Queue<int>();                   // what the next bytes written are

        void DmaFollowers(int value, int mask, params int[] fields)
        {
            for (int i = 0; i < fields.Length; i++) if ((value & (mask << i)) != 0) dmaNext.Enqueue(fields[i]);
        }

        // Returns true if the transfer was done here, so that CSpect does not start it
        bool DmaWrite(byte v)
        {
            if (dmaNext.Count > 0)
            {
                switch (dmaNext.Dequeue())
                {
                    case 0: dmaA = (dmaA & 0xFF00) | v; break;
                    case 1: dmaA = (dmaA & 0x00FF) | (v << 8); break;
                    case 2: dmaLength = (dmaLength & 0xFF00) | v; break;
                    case 3: dmaLength = (dmaLength & 0x00FF) | (v << 8); break;
                    case 4: dmaB = (dmaB & 0xFF00) | v; break;
                    case 5: dmaB = (dmaB & 0x00FF) | (v << 8); break;
                    case 6: if ((v & 0x20) != 0) dmaNext.Enqueue(7); break;   // port B timing: the prescaler follows
                    case 7: dmaTimed = v != 0; break;
                }
                return false;
            }
            if ((v & 0x80) == 0)
            {
                if ((v & 3) != 0)              // WR0
                {
                    dmaAtoB = (v & 4) != 0;
                    DmaFollowers(v, 8, 0, 1, 2, 3);
                }
                else if ((v & 7) == 4)         // WR1, port A
                {
                    dmaAIo = (v & 8) != 0;
                    dmaAMode = (v >> 4) & 3;
                    if ((v & 0x40) != 0) dmaNext.Enqueue(8);
                }
                else if ((v & 7) == 0)         // WR2, port B
                {
                    dmaBIo = (v & 8) != 0;
                    dmaBMode = (v >> 4) & 3;
                    if ((v & 0x40) != 0) dmaNext.Enqueue(6);
                }
                return false;
            }
            if ((v & 3) == 1)                  // WR4
            {
                DmaFollowers(v, 4, 4, 5);
                return false;
            }
            if ((v & 3) != 3) return false;
            if (v == 0xBB) { dmaNext.Enqueue(8); return false; }     // read mask
            if (v == 0xC3) { dmaTimed = false; return false; }       // reset
            if (v != 0x87 || !nmiBreaksDma || dmaTimed || dmaLength < DmaAtOnce) return false;

            int src = dmaAtoB ? dmaA : dmaB, dst = dmaAtoB ? dmaB : dmaA;
            int srcMode = dmaAtoB ? dmaAMode : dmaBMode, dstMode = dmaAtoB ? dmaBMode : dmaAMode;
            if (dmaAtoB ? dmaAIo : dmaBIo) return false;             // only from memory
            bool toIo = dmaAtoB ? dmaBIo : dmaAIo;
            // only the sprite ports: OutPort to other ports does not work like the DMA (palette data at $253B
            // gives wrong colours)
            if (toIo && dst != 0x57 && dst != 0x5B) return false;
            busy = true;
            for (int i = 0; i < dmaLength; i++)
            {
                byte d = CSpect.Peek((ushort)src);
                if (toIo) CSpect.OutPort((ushort)dst, d); else CSpect.Poke((ushort)dst, d);
                if (srcMode == 0) src--; else if (srcMode == 1) src++;
                if (dstMode == 0) dst--; else if (dstMode == 1) dst++;
            }
            busy = false;
            return true;
        }

        // Not in iPlugin in every version of CSpect: virtual, so that the same DLL loads in all of them.
        public virtual string GetName() { return "Compat"; }
        public virtual string GetDescription() { return "Fixes some incompatibilities between CSpect and games for the Next"; }
        public void OSTick() { }
        public bool KeyPressed(int _id) { return false; }
        public void Quit() { if (music != null) music.Close(); }
    }
}
