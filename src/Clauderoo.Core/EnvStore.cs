namespace Clauderoo.Core;

public interface IEnvStore
{
    string? GetProcess(string name);
    string? GetUser(string name);
    /// <summary>Sets the User-scope variable; a null value deletes it.</summary>
    void SetUser(string name, string? value);
}
