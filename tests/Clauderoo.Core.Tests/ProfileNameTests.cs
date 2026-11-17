namespace Clauderoo.Core.Tests;

public class ProfileNameTests
{
    [Theory]
    [InlineData("work")]
    [InlineData("my-work2")]
    [InlineData("work-")]
    public void Validate_Accepts(string name) => ProfileName.Validate(name);

    [Theory]
    [InlineData("")]
    [InlineData("Work")]
    [InlineData("a b")]
    [InlineData("..")]
    [InlineData("_removed")]
    public void Validate_Rejects(string name) =>
        Assert.Throws<ClauderooException>(() => ProfileName.Validate(name));
}

public class ProfileNameStrictTests
{
    [Fact]
    public void Validate_RejectsTrailingNewline() =>
        Assert.Throws<ClauderooException>(() => ProfileName.Validate("work\n"));

    [Fact]
    public void Validate_RejectsNull() =>
        Assert.Throws<ClauderooException>(() => ProfileName.Validate(null!));
}

public class ProfileNameDashTests
{
    [Fact]
    public void Validate_LeadingDash_PlainMessage() =>
        Assert.Equal("Names can't start with -.",
            Assert.Throws<ClauderooException>(() => ProfileName.Validate("-x")).Message);

    [Fact]
    public void InputDialogRule_LeadingDash() =>
        Assert.Equal("Names can't start with -.", Clauderoo.Tray.InputDialog.ProfileNameRule([])("-x"));
}