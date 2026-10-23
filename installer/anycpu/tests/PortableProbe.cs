// Run from a folder holding Finestra.exe: prints where StoragePaths keeps the data.
// It reads StoragePaths ONLY when the portable sentinel is there: without it AppDataDir would resolve - and create,
// and possibly migrate into - the real Documents\Finestra, which a test must never touch.
static class PortableProbe
{
    static int Main()
    {
        string dir = System.IO.Path.GetDirectoryName(typeof(Finestra.Core.StoragePaths).Assembly.Location);
        if (!System.IO.File.Exists(System.IO.Path.Combine(dir, Finestra.Core.StoragePaths.PortableSentinel)))
        {
            System.Console.WriteLine("NO-SENTINEL");
            return 3;
        }
        System.Console.WriteLine(Finestra.Core.StoragePaths.AppDataDir);
        System.Console.WriteLine(Finestra.Core.StoragePaths.ConnectionsFile);
        return 0;
    }
}
