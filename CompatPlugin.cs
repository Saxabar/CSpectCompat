// Compatibility plugin for CSpect 3.
// Some games that worked in CSpect 2 crash in CSpect 3 because of changed defaults. The fixes are chosen
// per game with the CSPECT_COMPAT environment variable (comma-separated list); without it the plugin
// does nothing.
//
//   l2bank9   Layer 2 starts at 16K bank 9 (Next register $12), as in older cores. CSpect 3 sets it to 8
//             when it loads a .snx; games that never set it (e.g. Montana Mike) then draw over their data.
using System;
using System.Collections.Generic;
using Plugin;

namespace Compat
{
    public class CompatPlugin : iPlugin
    {
        iCSpect CSpect;
        bool l2bank9;
        long frame;

        public List<sIO> Init(iCSpect _CSpect)
        {
            CSpect = _CSpect;
            string opts = Environment.GetEnvironmentVariable("CSPECT_COMPAT") ?? "";
            foreach (string o in opts.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (o.Equals("l2bank9", StringComparison.OrdinalIgnoreCase)) l2bank9 = true;
                else Console.WriteLine(" Compat: unknown option " + o);
            }
            if (l2bank9) Console.WriteLine(" Compat added (l2bank9)");
            return new List<sIO>();
        }

        public void Tick()
        {
            // first frame: after CSpect has loaded the file and set its registers
            if (++frame == 1 && l2bank9) CSpect.SetNextRegister(0x12, 9, 0);
        }

        public void Reset() { frame = 0; }
        public void OSTick() { }
        public bool KeyPressed(int _id) { return false; }
        public bool Write(eAccess _type, int _port, int _id, byte _value) { return false; }
        public byte Read(eAccess _type, int _port, int _id, out bool _isvalid) { _isvalid = false; return 0; }
        public void Quit() { }
    }
}
