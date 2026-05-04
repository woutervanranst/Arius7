using Arius.Explorer.Settings;
using Arius.Explorer.Shared.Extensions;

namespace Arius.Explorer.Tests.Settings;

public class RepositoryOptionsTests
{
    [Test]
    public void AccountKeyAndPassphrase_WhenProtectedValuesAreEmpty_ReturnEmptyStrings()
    {
        var repository = new RepositoryOptions();

        repository.AccountKey.ShouldBe(string.Empty);
        repository.Passphrase.ShouldBe(string.Empty);
    }

    [Test]
    public void AccountKeyAndPassphrase_WhenProtectedValuesRoundTrip_ReturnOriginalValues()
    {
        var repository = new RepositoryOptions
        {
            AccountKeyProtected = "secret-key".Protect(),
            PassphraseProtected = "secret-pass".Protect(),
        };

        repository.AccountKey.ShouldBe("secret-key");
        repository.Passphrase.ShouldBe("secret-pass");
    }

}
