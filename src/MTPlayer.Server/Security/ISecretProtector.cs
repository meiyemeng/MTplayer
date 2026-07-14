namespace MTPlayer.Server.Security;

public interface ISecretProtector
{
    string Protect(string plaintext);

    string Unprotect(string encoded);
}
