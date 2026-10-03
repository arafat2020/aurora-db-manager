using AuroraDbManager.Api.Domain.Databases;

namespace AuroraDbManager.Api.Tests.Databases;

public sealed class DatabaseNameTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("a")]
    [InlineData("app")]
    [InlineData("app_1")]
    [InlineData("analytics_2026_archive")]
    [InlineData("a_")]
    [InlineData("postgres_app")]
    public void Validate_ValidName_ReturnsNoError(string name)
    {
        Assert.Null(DatabaseName.Validate(name));
    }

    [Fact]
    public void Validate_NameOfMaximumLength_ReturnsNoError()
    {
        Assert.Null(DatabaseName.Validate(new string('a', DatabaseName.MaxLength)));
    }

    [Fact]
    public void Validate_NameOverMaximumLength_ReturnsError()
    {
        Assert.NotNull(DatabaseName.Validate(new string('a', DatabaseName.MaxLength + 1)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\n")]
    public void Validate_MissingName_ReportsItAsRequired(string? name)
    {
        Assert.Equal("name is required.", DatabaseName.Validate(name));
    }

    [Theory]
    [InlineData("App")]
    [InlineData("APP")]
    [InlineData("1app")]
    [InlineData("_app")]
    [InlineData("my-db")]
    [InlineData("my db")]
    [InlineData(" app")]
    [InlineData("app ")]
    [InlineData("app.public")]
    [InlineData("app;drop")]
    [InlineData("app'")]
    [InlineData("\"app\"")]
    [InlineData("`app`")]
    [InlineData("app$")]
    [InlineData("naïve")]
    [InlineData("база")]
    [InlineData("app\0")]
    public void Validate_InvalidCharacters_ReturnsError(string name)
    {
        Assert.NotNull(DatabaseName.Validate(name));
    }

    [Theory]
    [InlineData("postgres")]
    [InlineData("template0")]
    [InlineData("template1")]
    [InlineData("mysql")]
    [InlineData("sys")]
    [InlineData("information_schema")]
    [InlineData("performance_schema")]
    public void Validate_EngineSystemDatabase_ReturnsError(string name)
    {
        Assert.NotNull(DatabaseName.Validate(name));
    }

    [Fact]
    public void Create_ValidName_SetsFields()
    {
        var instanceId = Guid.NewGuid();

        var database = Database.Create(instanceId, "app", Now);

        Assert.NotEqual(Guid.Empty, database.Id);
        Assert.Equal(instanceId, database.InstanceId);
        Assert.Equal("app", database.Name);
        Assert.Equal(Now, database.CreatedAt);
        Assert.Equal(Now, database.UpdatedAt);
    }

    [Theory]
    [InlineData("")]
    [InlineData("my-db")]
    [InlineData(" app ")]
    public void Create_InvalidName_Throws(string name)
    {
        Assert.Throws<ArgumentException>(() => Database.Create(Guid.NewGuid(), name, Now));
    }

    [Fact]
    public void Create_EmptyInstanceId_Throws()
    {
        Assert.Throws<ArgumentException>(() => Database.Create(Guid.Empty, "app", Now));
    }
}
