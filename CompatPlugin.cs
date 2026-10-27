// Compatibility plugin for CSpect 3.
// Some games that worked in CSpect 2, or on the Next, crash in CSpect 3. The fixes are chosen per game with
// the CSPECT_COMPAT environment variable (comma-separated list). Without it, they come from Compat.ini next
// to Compat.dll: the options of the file CSpect has loaded, found by its MD5, or the default= options.
//
//   l2bank9   Layer 2 starts at 16K bank 9 (Next register $12), as in older cores. CSpect 3 sets it to 8
//             when it loads a .snx; games that never set it (e.g. Montana Mike) then draw over their data.
//   nodivmmc  DivMMC automap off (bit 4 of Next register $0A). CSpect 3 maps the DivMMC memory when the
//             CPU reaches $0038 even with RAM in place of the ROM; a game that calls code there (e.g. Deltas
//             Shadow) then runs the DivMMC memory, which CSpect fills with random values.
//   dd01nop   DD 01 in the game's .nex is a 2-byte NOP, as in CSpect 2, where it was the debugger's BREAK
//             opcode (skipped when the debugger was off). CSpect 3 uses FD 00 for BREAK, so breakpoints
//             left in a game (e.g. Elite Next) run as LD BC,nn and swallow the next instruction. The .nex
//             is the one in CSPECT_COMPAT_FILE, or else the one CSpect has loaded; the bytes are changed in
//             memory only, before the game starts.
//   zeroram   RAM outside the 128K snapshot (8K pages 16-255) cleared before the game starts, as in CSpect 2.
//             CSpect 3 fills it with random values, and a game that shows Layer 2 without clearing it
//             (e.g. Spice Invaderz) shows them. Only for .snx/.sna games: a .nex can load banks there.
//   divfirst  DivMMC memory comes before Layer 2 mapping, as on the Next. With Layer 2 write-mapped at $0000
//             (port $123B bit 0), CSpect 3 sends the writes of DivMMC code (NextZXOS's interrupt handler and
//             tape traps) to Layer 2, so NextZXOS loses its saved stack pointer and the machine resets (e.g.
//             4K Race Next, whose loader copies its screen into Layer 2 while loading from tape). While DivMMC
//             code runs, those writes are kept by the plugin and given back when it reads them.
//   im2ff     IM 2 vector read at I*256+$FF, as on the Next (the bus reads $FF). CSpect 3 reads it at I*256+$00,
//             so a game that sets only the vector at $xxFF (e.g. 4K Race Next, Manic Miner Trick Or Treat AY)
//             jumps to a random address. CSpect calls the plugins' Tick right after it has taken the frame
//             interrupt: if the CPU has just jumped through the vector at $xx00, it is sent through $xxFF.
//   sdstream  streaming from the SD card done by the plugin. NextZXOS's DISK_STRMSTART fails in CSpect 3, and a
//             game that streams its music from the SD card (e.g. The Way of the Exploding Fist) stops on a
//             black screen. RST $08 DISK_STRMSTART / DISK_STRMEND return at once without error; while the
//             stream is on, the game's own reads of the SD card port $EB get the file's blocks, each followed
//             by 2 CRC bytes and the data token of the next one. The file is the one the game mapped with
//             DISK_FILEMAP, read from the game's folder on the PC (silence if it is not there).
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
        bool l2bank9, nodivmmc, dd01nop, zeroram, divfirst, im2ff, sdstream, cleared;
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
            l2bank9 = nodivmmc = dd01nop = zeroram = divfirst = im2ff = sdstream = false;
            foreach (string o in opts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (o.Equals("l2bank9", StringComparison.OrdinalIgnoreCase)) l2bank9 = true;
                else if (o.Equals("nodivmmc", StringComparison.OrdinalIgnoreCase)) nodivmmc = true;
                else if (o.Equals("dd01nop", StringComparison.OrdinalIgnoreCase)) dd01nop = true;
                else if (o.Equals("zeroram", StringComparison.OrdinalIgnoreCase)) zeroram = true;
                else if (o.Equals("divfirst", StringComparison.OrdinalIgnoreCase)) divfirst = true;
                else if (o.Equals("im2ff", StringComparison.OrdinalIgnoreCase)) im2ff = true;
                else if (o.Equals("sdstream", StringComparison.OrdinalIgnoreCase)) sdstream = true;
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
            if (dd01nop || zeroram)
            {
                // CSpect writes the Next registers while it loads the file: the patch and the RAM clear are
                // done at those writes (frame 0), so that they are in place before the game's first instruction
                for (int r = 0; r < 256; r++) io.Add(new sIO(r, eAccess.NextReg_Write, 0, 0));
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
            else if (l2bank9 || nodivmmc || dd01nop || zeroram || divfirst || im2ff) Console.WriteLine(" Compat added (" + opts.Trim() + ")");
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

        // bank 5 (pages 10-11) is kept apart by CSpect 3: it is written at $4000, where it is when a file loads
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
                else if (_port == 0x00E3) divBank = _value & 0x0F;
                return false;
            }
            if (_type == eAccess.Memory_Write)
            {
                if (busy) return false;
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
            // sdstream: the game reads the stream itself from the SD card port: 512 bytes of each block, 2 CRC
            // bytes, then $FF until the data token $FE of the next block. NextZXOS's own reads ($0000-$3FFF) are
            // not touched.
            if (_type == eAccess.Port_Read)
            {
                if (!streaming || CSpect.GetRegs().PC < 0x4000) return 0;
                _isvalid = true;
                return StreamByte();
            }
            if (_type == eAccess.Memory_Read)
            {
                byte v;
                if (divActive && divRam.Count > 0 && divRam.TryGetValue(divBank * 8192 + (_port - 0x2000), out v)) { _isvalid = true; return v; }
                return 0;
            }
            if (_type != eAccess.Memory_EXE) return 0;
            if (sdstream && _port == 0x0008 && SkipStream()) return 0;
            // divfirst: DivMMC code runs from an entry point (with the ROM at $0000 and automap on) to $1FF8-$1FFF
            bool exit = _port >= 0x1FF8 && _port <= 0x1FFF;
            if (!divActive && !exit)
            {
                if (CSpect.GetNextRegister(0x50, 0) == 0xFF && (CSpect.GetNextRegister(0x0A, 0) & 0x10) != 0) divActive = true;
            }
            else if (divActive && exit) divActive = false;
            return 0;
        }

        // CSpect resets after loading a file, before the loader's register writes; the RAM is cleared once only,
        // not again after F3
        public void Reset()
        {
            frame = 0; divActive = false; streaming = false;
            if (fromIni && byMd5.Count > 0) Identify();
            else if (dd01nop && breakpoints == null) breakpoints = FindBreakpoints(LoadedFile());
        }
        public void OSTick() { }
        public bool KeyPressed(int _id) { return false; }
        public void Quit() { if (music != null) music.Close(); }
    }
}
