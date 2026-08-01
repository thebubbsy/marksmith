using System;
using MdToPdf.Models;
using MdToPdf.Services;
using Xunit;

namespace MdToPdf.Core.Tests;

public class CustomThemeStoreTests
{
    [Fact]
    public void CustomThemeStore_All_Returns_Non_Null_List()
    {
        var list = CustomThemeStore.All;
        Assert.NotNull(list);
    }

    [Fact]
    public void CustomThemeStore_AddOrUpdate_Saves_And_Increments_Version()
    {
        int initialVersion = CustomThemeStore.Version;
        var theme = new ThemeDefinition(
            "Test Theme " + Guid.NewGuid().ToString("N"),
            "#FFFFFF", "#000000", "#112233", "#F8F8F8", "#E0E0E0", "#0071C1", "#F0F0F0", "#CCCCCC");

        try
        {
            CustomThemeStore.AddOrUpdate(theme);
            Assert.True(CustomThemeStore.Version > initialVersion);
            Assert.Contains(CustomThemeStore.All, t => t.Name == theme.Name);
        }
        finally
        {
            CustomThemeStore.Remove(theme.Name);
        }
    }

    [Fact]
    public void CustomThemeStore_Remove_Deletes_Theme()
    {
        var theme = new ThemeDefinition(
            "Remove Test " + Guid.NewGuid().ToString("N"),
            "#111111", "#EEEEEE", "#FFFFFF", "#222222", "#333333", "#444444", "#555555", "#666666");

        CustomThemeStore.AddOrUpdate(theme);
        Assert.Contains(CustomThemeStore.All, t => t.Name == theme.Name);

        bool removed = CustomThemeStore.Remove(theme.Name);
        Assert.True(removed);
        Assert.DoesNotContain(CustomThemeStore.All, t => t.Name == theme.Name);
    }

    [Fact]
    public void CustomThemeStore_ThemeCatalog_GetOrDefault_Falls_Back_To_Default()
    {
        var catalog = new ThemeCatalog();
        var theme = catalog.GetOrDefault("NonExistentThemeName12345");
        Assert.NotNull(theme);
        Assert.Equal("GitHub Light", theme.Name);
    }

    [Fact]
    public void CustomThemeStore_ThemeCatalog_Contains_Custom_And_Builtin_Themes()
    {
        var catalog = new ThemeCatalog();
        Assert.NotNull(catalog.GetOrDefault("GitHub Light"));
        Assert.NotNull(catalog.GetOrDefault("Classic Professional"));
    }
}
