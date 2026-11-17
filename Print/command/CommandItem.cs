using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MHG_BoxPrint.Print.command
{
    public class CommandItem
    {
        public string Command { get; set; }
        public int DelayMs { get; set; }

        public CommandItem(string command, int delayMs)
        {
            Command = command;
            DelayMs = delayMs;
        }
    }
}
