internal static class VerifyUpdateTest
{
    static void Main(string[] args)
    {
        byte[] manifest=File.ReadAllBytes(args[0]),signature=File.ReadAllBytes(args[1]);
        Program.VerifySignature(manifest,signature);
        manifest[0]^=1;
        try { Program.VerifySignature(manifest,signature); }
        catch(InvalidDataException) { Console.WriteLine("PASS valid signature and tampered manifest rejected"); return; }
        throw new Exception("Tampering went undetected");
    }
}
