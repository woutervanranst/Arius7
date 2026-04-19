using System;
using System.Globalization;
using Arius.Explorer.Shared.Converters;

namespace Arius.Explorer.Tests.Shared.Converters;

public class BytesToReadableSizeConverterTests
{
    private static readonly BytesToReadableSizeConverter Converter = new();

    [Test]
    public void Convert_WhenValueIsLong_ReturnsReadableSize()
    {
        var result = Converter.Convert(1536L, typeof(string), null!, CultureInfo.InvariantCulture);

        result.ShouldBeOfType<string>();
        ((string)result).ShouldContain("KB");
    }

    [Test]
    public void Convert_WhenValueIsNotLong_ReturnsNull()
    {
        var result = Converter.Convert("1536", typeof(string), null!, CultureInfo.InvariantCulture);

        result.ShouldBeNull();
    }

    [Test]
    public void ConvertBack_AlwaysThrows()
    {
        Should.Throw<NotImplementedException>(() => Converter.ConvertBack("1.5 KB", typeof(long), null!, CultureInfo.InvariantCulture));
    }
}
