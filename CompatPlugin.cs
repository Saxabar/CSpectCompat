// Compatibility plugin for CSpect 3.
// Some games that worked in CSpect 2 crash in CSpect 3 because of changed defaults. The fixes are chosen
// per game with the CSPECT_COMPAT environment variable (comma-separated list); without it the plugin
// does nothing.
//
//   l2bank9   Layer 2 starts at 16K bank 9 (Next register $12), as in older cores. CSpect 3 sets it to 8
//             when it loads a .snx; games that never set it (e.g. Montana Mike) then draw over their data.
//   nodivmmc  DivMMC automap off (bit 4 of Next register $0A). CSpect 3 maps the DivMMC memory when the
//             CPU reaches $0038 even with RAM in place of the ROM; a game that calls code there (e.g. Deltas
//             Shadow) then runs the DivMMC memory, which CSpect fills with random values.
//   dd01nop   DD 01 in the game's .nex is a 2-byte NOP, as in CSpect 2, where it was the debugger's BREAK
//             opcode (skipped when the debugger was off). CSpect 3 uses FD 00 for BREAK, so breakpoints
//             left in a game (e.g. Elite Next) run as LD BC,nn and swallow the next instruction. The .nex
//             is given in CSPECT_COMPAT_FILE; the bytes are changed in memory only, before the game starts.
//   zeroram   RAM outside the 128K snapshot (8K pages 16-255) cleared before the game starts, as in CSpect 2.
//             CSpect 3 fills it with random values, and a game that shows Layer 2 without clearing it
//             (e.g. Spice Invaderz) shows them. Only for .snx/.sna games: a .nex can load banks there.
using System;
using System.Collections.Generic;
using System.IO;
using Plugin;

namespace Compat
{
    public class CompatPlugin : iPlugin
    {
        iCSpect CSpect;
        bool l2bank9, nodivmmc, dd01nop, zeroram, cleared;
        long frame;
        List<int> breakpoints;         // DD 01 positions: 16K bank * 16384 + offset
        int patched;

        public List<sIO> Init(iCSpect _CSpect)
        {
            CSpect = _CSpect;
            List<sIO> io = new List<sIO>();
            string opts = Environment.GetEnvironmentVariable("CSPECT_COMPAT") ?? "";
            foreach (string o in opts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (o.Equals("l2bank9", StringComparison.OrdinalIgnoreCase)) l2bank9 = true;
                else if (o.Equals("nodivmmc", StringComparison.OrdinalIgnoreCase)) nodivmmc = true;
                else if (o.Equals("dd01nop", StringComparison.OrdinalIgnoreCase)) dd01nop = true;
                else if (o.Equals("zeroram", StringComparison.OrdinalIgnoreCase)) zeroram = true;
                else Console.WriteLine(" Compat: unknown option " + o);
            }
            if (dd01nop) breakpoints = FindBreakpoints(Environment.GetEnvironmentVariable("CSPECT_COMPAT_FILE"));
            if (dd01nop || zeroram)
            {
                // CSpect writes the Next registers while it loads the file: the patch and the RAM clear are
                // done at those writes (frame 0), so that they are in place before the game's first instruction
                for (int r = 0; r < 256; r++) io.Add(new sIO(r, eAccess.NextReg_Write, 0, 0));
            }
            if (l2bank9 || nodivmmc || dd01nop || zeroram) Console.WriteLine(" Compat added (" + opts.Trim() + ")");
            return io;
        }

        // DD 01 in the banks of a .nex file (header 512 bytes, then the loading screens, then the banks
        // present in the order 5, 2, 0, 1, 3, 4, 6, 7 ... 111)
        static List<int> FindBreakpoints(string file)
        {
            List<int> found = new List<int>();
            if (string.IsNullOrEmpty(file) || !File.Exists(file) || !file.EndsWith(".nex", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine(" Compat: dd01nop needs the .nex in CSPECT_COMPAT_FILE");
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

        public bool Write(eAccess _type, int _port, int _id, byte _value)
        {
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
            // first frame: after CSpect has loaded the file and set its registers
            if (++frame != 1) return;
            if (dd01nop)
            {
                PatchBreakpoints();
                Console.WriteLine(" Compat: " + patched + " of " + breakpoints.Count + " DD 01 breakpoints turned into NOPs");
            }
            if (l2bank9) CSpect.SetNextRegister(0x12, 9, 0);
            if (nodivmmc) CSpect.SetNextRegister(0x0A, (byte)(CSpect.GetNextRegister(0x0A, 0) & ~0x10), 0);
        }

        public void Reset() { frame = 0; }          // the RAM is cleared once only: not again after F3
        public void OSTick() { }
        public bool KeyPressed(int _id) { return false; }
        public byte Read(eAccess _type, int _port, int _id, out bool _isvalid) { _isvalid = false; return 0; }
        public void Quit() { }
    }
}
