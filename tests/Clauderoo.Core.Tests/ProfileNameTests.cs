namespace Clauderoo.Core.Tests;

public class ProfileNameTests
{
    [Theory]
    [InlineData("work")]
    [InlineData("my-work2")]
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
