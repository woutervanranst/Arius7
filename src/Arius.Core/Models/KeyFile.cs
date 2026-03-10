namespace Arius.Core.Models;

public sealed record KeyFile(string Salt, int Iterations, string PassphraseHash);
