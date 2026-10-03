// Holds something open on behalf of a test, as another program would, until
// the test closes this process's standard input.
//
//   Vaktari.LockHolder file <path> <ready>   open <path> with FileShare.None
//   Vaktari.LockHolder cwd <folder> <ready>  make <folder> the current folder
//
// <ready> is written once the hold is in place, so a test waits on that file
// rather than on a sleep. Exits when its standard input closes, which is how a
// test that started it stops it; a test kills it only if it does not.

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: Vaktari.LockHolder file|cwd <path> <ready>");
    return 2;
}

var (mode, path, ready) = (args[0], args[1], args[2]);

FileStream? held = null;

switch (mode)
{
    case "file":
        held = new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        break;

    case "cwd":
        Environment.CurrentDirectory = path;
        break;

    default:
        Console.Error.WriteLine($"unknown mode: {mode}");
        return 2;
}

File.WriteAllText(ready, Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));

// Until the test lets go of this end of the pipe.
while (Console.In.ReadLine() is not null) { }

held?.Dispose();

return 0;
