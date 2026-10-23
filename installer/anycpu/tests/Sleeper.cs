// Stand-in for Finestra.exe / wfreerdp.exe in the installer's in-use test: it just stays alive.
static class Sleeper { static void Main() { System.Threading.Thread.Sleep(60000); } }
