using DotNetBoost.Settings.Core;

namespace DotNetBoost.Settings.UnitTests;

public class ExtensionsTests
{
    [Theory]
    [InlineData("587", 587)]
    [InlineData("-1", -1)]
    [InlineData("0", 0)]
    public void ConvertTo_Int_Works(string input, int expected)
        => Assert.Equal(expected, Extensions.ConvertTo<int>(input));

    [Fact]
    public void ConvertTo_Bool_True() => Assert.True((bool)Extensions.ConvertTo(typeof(bool), "true")!);

    [Fact]
    public void ConvertTo_Bool_False() => Assert.False((bool)Extensions.ConvertTo(typeof(bool), "false")!);

    [Fact]
    public void ConvertTo_String_ReturnsSame() => Assert.Equal("hello", Extensions.ConvertTo(typeof(string), "hello"));

    [Fact]
    public void ConvertTo_Decimal() => Assert.Equal(12.50m, Extensions.ConvertTo<decimal>("12.50"));

    [Fact]
    public void ConvertTo_Guid_Works()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id, Extensions.ConvertTo<Guid>(id.ToString()));
    }

    [Fact]
    public void ConvertTo_Enum_Works() => Assert.Equal(TestMode.Live, Extensions.ConvertTo<TestMode>("Live"));

    [Fact]
    public void ConvertTo_NullableInt_Works() => Assert.Equal(100, Extensions.ConvertTo<int?>("100"));

    [Fact]
    public void ConvertTo_NullableInt_Whitespace_ReturnsNull() => Assert.Null(Extensions.ConvertTo(typeof(int?), "   "));

    [Fact]
    public void ConvertTo_DateTime_Works()
    {
        var dt = new DateTime(2024, 6, 15, 10, 30, 0, DateTimeKind.Utc);
        var r  = Extensions.ConvertTo<DateTime>(dt.ToString("o"));
        Assert.Equal(dt.Date, r.Date);
    }

    [Fact]
    public void ConvertTo_TimeSpan_Works()
    {
        var ts = TimeSpan.FromMinutes(30);
        Assert.Equal(ts, Extensions.ConvertTo<TimeSpan>(ts.ToString()));
    }

    [Fact]
    public void ConvertTo_ComplexType_ViaJson()
    {
        var json = """{"Name":"Alice","Age":30}""";
        var r    = Extensions.ConvertTo<PersonDto>(json);
        Assert.Equal("Alice", r!.Name);
        Assert.Equal(30, r.Age);
    }

    [Fact]
    public void ConvertTo_InvalidInt_Throws()
        => Assert.ThrowsAny<Exception>(() => Extensions.ConvertTo(typeof(int), "not-a-number"));

    [Fact]
    public void TryConvertTo_InvalidInt_ReturnsFalse()
    {
        var ok = Extensions.TryConvertTo(typeof(int), "abc", out var r);
        Assert.False(ok);
        Assert.Null(r);
    }

    [Fact]
    public void TryConvertTo_ValidInt_ReturnsTrue()
    {
        var ok = Extensions.TryConvertTo(typeof(int), "42", out var r);
        Assert.True(ok);
        Assert.Equal(42, r);
    }

    [Fact]
    public void ConvertFrom_Int() => Assert.Equal("587", Extensions.ConvertFrom(typeof(int), 587));

    [Fact]
    public void ConvertFrom_Bool_True() => Assert.Equal("True", Extensions.ConvertFrom(typeof(bool), true), ignoreCase: true);

    [Fact]
    public void ConvertFrom_Guid()
    {
        var id = Guid.NewGuid();
        Assert.Equal(id.ToString(), Extensions.ConvertFrom(typeof(Guid), id));
    }

    [Fact]
    public void ConvertFrom_ComplexType_ProducesJson()
    {
        var dto = new PersonDto { Name = "Bob", Age = 25 };
        var r   = Extensions.ConvertFrom(typeof(PersonDto), dto);
        Assert.Contains("Bob", r);
        Assert.Contains("25", r);
    }

    [Fact]
    public void ConvertFrom_Generic_NullReturnsEmpty() => Assert.Equal(string.Empty, Extensions.ConvertFrom<string?>(null));

    [Theory]
    [InlineData(typeof(int),       false)]
    [InlineData(typeof(bool),      false)]
    [InlineData(typeof(string),    false)]
    [InlineData(typeof(Guid),      false)]
    [InlineData(typeof(decimal),   false)]
    [InlineData(typeof(DateTime),  false)]
    [InlineData(typeof(TimeSpan),  false)]
    [InlineData(typeof(int?),      false)]
    [InlineData(typeof(PersonDto), true)]
    public void NeedsSerialization_ReturnsExpected(Type type, bool expected)
        => Assert.Equal(expected, Extensions.NeedsSerialization(type));




    [Theory]
    [InlineData(typeof(int), 42)]
    [InlineData(typeof(bool), true)]
    [InlineData(typeof(decimal), 9.99)]
    public void RoundTrip_ConvertFrom_Then_ConvertTo(Type type, object value)
    {
        var s = Extensions.ConvertFrom(type, value);
        var d = Extensions.ConvertTo(type, s);
        Assert.Equal(value.ToString(), d!.ToString());
    }

    private enum TestMode { Test, Live }
    private sealed class PersonDto { public string? Name { get; set; } public int Age { get; set; } }
}
