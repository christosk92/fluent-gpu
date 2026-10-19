using System;
using FluentGpu.WindowsApi.Storage;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// <see cref="AppDataStore.ForUnpackaged"/> name validation (docs/plans/app-authoring-ergonomics-implementation.md §9):
/// publisher/product are ONE registry-key / folder segment each. A separator or surrounding whitespace used to nest the
/// key and folders silently; now it throws <see cref="ArgumentException"/> naming the parameter. Pure — constructing a
/// store touches neither the registry nor the file system.
/// </summary>
public sealed class AppDataStoreValidationTests
{
    [Theory]
    [InlineData(@"Contoso\Sub")]
    [InlineData("Contoso/Sub")]
    [InlineData(" Contoso")]
    [InlineData("Contoso ")]
    [InlineData("\tContoso")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    public void ForUnpackaged_RejectsBadPublisher_NamingTheParameter(string publisher)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => AppDataStore.ForUnpackaged(publisher, "MyApp"));
        Assert.Equal("publisher", ex.ParamName);
    }

    [Theory]
    [InlineData(@"MyApp\Settings")]
    [InlineData("My/App")]
    [InlineData("MyApp ")]
    [InlineData(" MyApp")]
    public void ForUnpackaged_RejectsBadProduct_NamingTheParameter(string product)
    {
        var ex = Assert.ThrowsAny<ArgumentException>(() => AppDataStore.ForUnpackaged("Contoso", product));
        Assert.Equal("product", ex.ParamName);
    }

    [Fact]
    public void ForUnpackaged_RejectsNull_NamingTheParameter()
    {
        Assert.Equal("publisher", Assert.Throws<ArgumentNullException>(() => AppDataStore.ForUnpackaged(null!, "MyApp")).ParamName);
        Assert.Equal("product", Assert.Throws<ArgumentNullException>(() => AppDataStore.ForUnpackaged("Contoso", null!)).ParamName);
    }

    [Theory]
    [InlineData("FluentGpu", "Gallery")]
    [InlineData("FluentGpu", "Smoke")]
    [InlineData("Wavee", "Wavee")]
    [InlineData("Contoso Ltd", "My App 2")]   // inner spaces / digits are fine
    public void ForUnpackaged_AcceptsRealCallerNames(string publisher, string product)
    {
        var store = AppDataStore.ForUnpackaged(publisher, product);
        Assert.Equal($@"HKCU\Software\{publisher}\{product}\Settings", store.RegistryPath);
    }
}
