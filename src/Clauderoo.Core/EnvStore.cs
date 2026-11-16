namespace Clauderoo.Core;

public interface IEnvStore
{
    string? GetProcess(string name);
    string? GetUser(string name);
    /// <summary>Sets the User-scope variable; a null value deletes it.</summary>
    void SetUser(string name, string? value);
}

/// <summary>Real environment access. Setting a User variable broadcasts the change to the system.</summary>
public class WindowsEnvStore : IEnvStore
{
    public string? GetProcess(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.Process);

    public string? GetUser(string name) =>
        Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User);

    public void SetUser(string name, string? value) =>
        Environment.SetEnvironmentVariable(name, value, EnvironmentVariableTarget.User);
}
