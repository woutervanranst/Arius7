using Arius.Explorer.Shared.Extensions;

namespace Arius.Explorer.Tests.Shared.Extensions;

public class DataProtectionExtensionsTests
{
    [Test]
    public void ProtectAndUnprotect_WhenTextIsEmpty_ReturnEmptyString()
    {
        string.Empty.Protect().ShouldBe(string.Empty);
        string.Empty.Unprotect().ShouldBe(string.Empty);
    }

    [Test]
    public void Unprotect_WhenValueIsNotBase64_ReturnsOriginalValue()
    {
        const string plainText = "not-base64";

        plainText.Unprotect().ShouldBe(plainText);
    }

    [Test]
    public void ProtectAndUnprotect_RoundTripPlainText()
    {
        const string plainText = "secret-value";

        var protectedValue = plainText.Protect();

        protectedValue.ShouldNotBeNull();
        protectedValue.Unprotect().ShouldBe(plainText);
    }
}
