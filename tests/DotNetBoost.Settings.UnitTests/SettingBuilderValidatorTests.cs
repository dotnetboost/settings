using DotNetBoost.Settings.Core;
using DotNetBoost.Settings.Core.Attributes;

namespace DotNetBoost.Settings.UnitTests;

public class SettingBuilderValidatorTests
{
    [Fact]
    public void Validate_UniqueTypes_DoesNotThrow()
    {
        var ex = Record.Exception(() =>
            SettingBuilderValidator.Validate(new[] { typeof(ValidMailSettings), typeof(ValidSmsSettings) }));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_EmptyTypeList_DoesNotThrow()
        => Assert.Null(Record.Exception(() => SettingBuilderValidator.Validate(Array.Empty<Type>())));

    [Fact]
    public void Validate_TypesWithoutAttribute_DoesNotThrow()
        => Assert.Null(Record.Exception(() => SettingBuilderValidator.Validate(new[] { typeof(string), typeof(int) })));

    [Fact]
    public void Validate_DuplicateRoute_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(DupRouteA), typeof(DupRouteB) }));
        Assert.Contains("Duplicate SettingGroup route names", ex.Message);
        Assert.Contains("dup-route", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateRoute_IsCaseInsensitive()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(RouteUpperCase), typeof(RouteLowerCase) }));
        Assert.Contains("Duplicate SettingGroup route names", ex.Message);
    }

    [Fact]
    public void Validate_SameClassNameInTwoNamespaces_Throws()
    {
        // Both resolve to the group name "MailSettings", so they would share rows.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[]
            {
                typeof(DotNetBoost.Settings.UnitTests.Settings.MailSettings),
                typeof(DotNetBoost.Settings.UnitTests.DuplicatedSettings.MailSettings)
            }));
        Assert.Contains("Duplicate settings group names", ex.Message);
        Assert.Contains("MailSettings", ex.Message);
    }

    [Fact]
    public void Validate_SameClassName_WithDistinctExplicitNames_DoesNotThrow()
    {
        // An explicit Name is exactly how you disambiguate same-named classes.
        Assert.Equal(typeof(SameNameA.Shared).Name, typeof(SameNameB.Shared).Name);   // premise

        var ex = Record.Exception(() =>
            SettingBuilderValidator.Validate(new[] { typeof(SameNameA.Shared), typeof(SameNameB.Shared) }));
        Assert.Null(ex);
    }

    [Fact]
    public void Validate_DistinctClassNames_CollidingOnExplicitName_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(CollidingNameA), typeof(CollidingNameB) }));
        Assert.Contains("Duplicate settings group names", ex.Message);
        Assert.Contains("shared-key", ex.Message);
    }

    [Fact]
    public void Validate_DuplicateGroupName_IsCaseInsensitive()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(CaseNameA), typeof(CaseNameB) }));
        Assert.Contains("Duplicate settings group names", ex.Message);
    }

    [Fact]
    public void Validate_BlankExplicitName_Throws()
    {
        // Silently falling back to the class name here would hide a typo.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(BlankNameSettings) }));
        Assert.Contains("Blank SettingGroup Name", ex.Message);
    }

    [Fact]
    public void Validate_EmptyRoute_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(EmptyRouteSettings) }));
        Assert.Contains("Empty SettingGroup route values", ex.Message);
    }

    [Fact]
    public void Validate_WhiteSpaceRoute_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            SettingBuilderValidator.Validate(new[] { typeof(WhiteSpaceRouteSettings) }));
        Assert.Contains("Empty SettingGroup route values", ex.Message);
    }

    [Fact]
    public void Validate_NullTypes_Throws()
        => Assert.Throws<ArgumentNullException>(() => SettingBuilderValidator.Validate((IEnumerable<Type>)null!));

    [Fact]
    public void Validate_NullAssemblies_Throws()
        => Assert.Throws<ArgumentNullException>(() => SettingBuilderValidator.Validate((IEnumerable<System.Reflection.Assembly>)null!));
}

[SettingGroup("mail")]      public class ValidMailSettings { public string Host { get; set; } = ""; }
[SettingGroup("sms")]       public class ValidSmsSettings  { public string ApiKey { get; set; } = ""; }
[SettingGroup("dup-route")] public class DupRouteA { }
[SettingGroup("dup-route")] public class DupRouteB { }
[SettingGroup("UPPER-ROUTE")] public class RouteUpperCase { }
[SettingGroup("upper-route")] public class RouteLowerCase { }
[SettingGroup("")]          public class EmptyRouteSettings { }
[SettingGroup("   ")]       public class WhiteSpaceRouteSettings { }

[SettingGroup("collide-a", Name = "shared-key")] public class CollidingNameA { }
[SettingGroup("collide-b", Name = "shared-key")] public class CollidingNameB { }
[SettingGroup("case-a", Name = "Shared-Key-Case")] public class CaseNameA { }
[SettingGroup("case-b", Name = "shared-key-case")] public class CaseNameB { }
[SettingGroup("blank-name", Name = "  ")] public class BlankNameSettings { }

// Nested so both nominally distinct types still report Type.Name == "Shared".
public static class SameNameA { [SettingGroup("shared-a", Name = "shared-a")] public class Shared { } }
public static class SameNameB { [SettingGroup("shared-b", Name = "shared-b")] public class Shared { } }
