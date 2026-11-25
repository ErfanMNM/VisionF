using ClassLibraryMT.Licensing;

const string machineId = "84b8871a-64b8-44b3-a538-59c65d6acc90";
const string equivalentMachineId = "84B8871A64B844B3A53859C65D6ACC90";
const string otherMachineId = "a5c37d6b-1ad4-489a-940b-1c28cf7dc293";

var first = HardwareId.Compute(machineId);
var same = HardwareId.Compute(equivalentMachineId);
var other = HardwareId.Compute(otherMachineId);

Check("Cung MachineGuid tao cung HWID", first == same);
Check("MachineGuid khac tao HWID khac", first != other);
Check("HWID co 32 ky tu hex", first.Length == 32 && first.All(Uri.IsHexDigit));
Check("MachineGuid rong bi tu choi", ThrowsForEmptyMachineId());
Check("HWID hien tai doc duoc tu may", HardwareId.Get().Length == 32);

static bool ThrowsForEmptyMachineId()
{
    try
    {
        HardwareId.Compute("");
        return false;
    }
    catch (ArgumentException)
    {
        return true;
    }
}

static void Check(string label, bool passed)
{
    Console.WriteLine($"[{(passed ? "OK" : "FAIL")}] {label}");
    if (!passed) Environment.ExitCode = 1;
}
