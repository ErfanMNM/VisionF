using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print.command
{
    public class CommandString
    {
        public const string Start = "START";
        public const string Stop = "STOP";
        public const string Clear = "CLR";
        public const string Trigger = "JET";
        public const string Status = "STATUS";
        public const string Counter = "CSTATUS";
        public const string ResetCounter = "RESET|1";
        public const string ResetPrintout= "RESET|2";
        public const string Ink = "INKV";
        public const  string Clean= "CLENOZ";
        public const string Nozzle = "NZSTA";

        public const string SFile = "SFILE|";
        public const string GFile = "GFILE";
        public const string MFD= "MFD|POD01:";
        public const string MFDLEN = "MFDLEN";

    }
}
