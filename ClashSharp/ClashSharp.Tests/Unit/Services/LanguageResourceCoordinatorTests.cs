using System.Globalization;
using ClashSharp.Model;
using ClashSharp.Service;

namespace ClashSharp.Tests.Unit.Services;

/// <summary>Exercises native language readback, publication ordering, and failure restoration.</summary>
public sealed class LanguageResourceCoordinatorTests
{
    /// <summary>Native resources must already agree when page bindings receive a language change.</summary>
    [Fact]
    public void Apply_VerifiesNativeOverrideBeforePublishingLanguage()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        string native = "en-US";
        int notifications = 0;
        localization.LanguageChanged += (_, _) =>
        {
            Assert.Equal("fr-FR", native);
            notifications++;
        };
        LanguageResourceCoordinator coordinator = new(localization, () => native, value => native = value);

        coordinator.Apply(AppLanguage.French);

        Assert.Equal(AppLanguage.French, coordinator.CaptureLanguage());
        Assert.Equal(1, notifications);
    }

    /// <summary>A native setter that reports success without applying the value cannot publish the selection.</summary>
    [Fact]
    public void Apply_UnobservedNativeWrite_PreservesCatalogAndDoesNotNotify()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        int notifications = 0;
        localization.LanguageChanged += (_, _) => notifications++;
        LanguageResourceCoordinator coordinator = new(localization, () => "en-US", _ => { });

        Assert.Throws<InvalidOperationException>(() => coordinator.Apply(AppLanguage.French));

        Assert.Equal(AppLanguage.English, coordinator.CaptureLanguage());
        Assert.Equal(0, notifications);
    }

    /// <summary>A native write rejection leaves the previously observed language usable.</summary>
    [Fact]
    public void Apply_NativeWriteThrows_PreservesOriginalFailureAndLanguage()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        IOException failure = new("native write rejected");
        LanguageResourceCoordinator coordinator = new(localization, () => "en-US", _ => throw failure);

        Assert.Same(failure, Assert.Throws<IOException>(() => coordinator.Apply(AppLanguage.German)));
        Assert.Equal(AppLanguage.English, coordinator.CaptureLanguage());
    }

    /// <summary>A failing text-binding observer restores both the old native language and catalog value.</summary>
    [Fact]
    public void Apply_PublicationThrows_RestoresBothLanguages()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        string native = "en-US";
        InvalidOperationException failure = new("binding rejected");
        localization.LanguageChanged += (_, _) =>
        {
            if (localization.CurrentLanguage == AppLanguage.French) { throw failure; }
        };
        LanguageResourceCoordinator coordinator = new(localization, () => native, value => native = value);

        Assert.Same(failure, Assert.Throws<InvalidOperationException>(() => coordinator.Apply(AppLanguage.French)));

        Assert.Equal("en-US", native);
        Assert.Equal(AppLanguage.English, coordinator.CaptureLanguage());
    }

    /// <summary>Independent readback catches native state changed by an observer after publication.</summary>
    [Fact]
    public void Apply_ObserverChangesNativeLanguage_RejectsAndRestores()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        string native = "en-US";
        localization.LanguageChanged += (_, _) =>
        {
            if (localization.CurrentLanguage == AppLanguage.French) { native = "de-DE"; }
        };
        LanguageResourceCoordinator coordinator = new(localization, () => native, value => native = value);

        Assert.Throws<InvalidOperationException>(() => coordinator.Apply(AppLanguage.French));

        Assert.Equal(AppLanguage.English, coordinator.CaptureLanguage());
    }

    /// <summary>A rollback failure remains visible instead of acknowledging a mixed language configuration.</summary>
    [Fact]
    public void Apply_NativeRestorationFails_PreservesBothFailures()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        string native = "en-US";
        bool rejectRestoration = false;
        InvalidOperationException publicationFailure = new("binding rejected");
        IOException restorationFailure = new("native restoration rejected");
        localization.LanguageChanged += (_, _) =>
        {
            if (localization.CurrentLanguage == AppLanguage.French)
            {
                rejectRestoration = true;
                throw publicationFailure;
            }
        };
        LanguageResourceCoordinator coordinator = new(localization, () => native, value =>
        {
            if (rejectRestoration) { throw restorationFailure; }
            native = value;
        });

        AggregateException failure = Assert.Throws<AggregateException>(() => coordinator.Apply(AppLanguage.French));

        Assert.Contains(publicationFailure, failure.InnerExceptions);
        Assert.Contains(restorationFailure, failure.InnerExceptions);
        Assert.Equal(AppLanguage.English, localization.CurrentLanguage);
        Assert.Throws<InvalidOperationException>(() => coordinator.CaptureLanguage());
    }

    /// <summary>Reapplying the catalog selection repairs an independently changed native override.</summary>
    [Fact]
    public void Apply_SameCatalogWithNativeDrift_RepairsOverrideWithoutRepublishing()
    {
        LocalizationService localization = new() { CurrentLanguage = AppLanguage.English };
        string native = "de-DE";
        int notifications = 0;
        localization.LanguageChanged += (_, _) => notifications++;
        LanguageResourceCoordinator coordinator = new(localization, () => native, value => native = value);

        Assert.Throws<InvalidOperationException>(() => coordinator.CaptureLanguage());
        coordinator.Apply(AppLanguage.English);

        Assert.Equal(AppLanguage.English, coordinator.CaptureLanguage());
        Assert.Equal(0, notifications);
    }

    /// <summary>First-use rollback uses the effective automatic language because the pinned native API rejects empty tags.</summary>
    [Fact]
    public void Apply_FirstPublicationFails_RestoresEffectiveAutomaticLanguage()
    {
        CultureInfo beforeCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("de-DE");
            LocalizationService localization = new();
            string native = "";
            List<string> writes = [];
            localization.LanguageChanged += (_, _) =>
            {
                if (localization.CurrentLanguage == AppLanguage.French) { throw new InvalidOperationException("binding rejected"); }
            };
            LanguageResourceCoordinator coordinator = new(localization, () => native, value => { writes.Add(value); native = value; });

            Assert.Throws<InvalidOperationException>(() => coordinator.Apply(AppLanguage.French));

            Assert.Equal(["fr-FR", "de-DE"], writes);
            Assert.Equal(AppLanguage.AutoDetect, coordinator.CaptureLanguage());
        }
        finally { CultureInfo.CurrentUICulture = beforeCulture; }
    }

    /// <summary>Automatic detection is resolved from the current UI culture instead of a stale native override.</summary>
    [Fact]
    public void Apply_AutoDetectAfterCultureChange_ReplacesOldProjection()
    {
        CultureInfo beforeCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("zh-HK");
            LocalizationService localization = new();
            string native = "fr-FR";
            LanguageResourceCoordinator coordinator = new(localization, () => native, value => native = value);

            coordinator.Apply(AppLanguage.AutoDetect);
            Assert.Equal("zh-TW", native);

            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("fa-IR");
            coordinator.Apply(AppLanguage.AutoDetect);

            Assert.Equal("fa-IR", native);
            Assert.Equal(AppLanguage.AutoDetect, coordinator.CaptureLanguage());
        }
        finally { CultureInfo.CurrentUICulture = beforeCulture; }
    }

    /// <summary>Unknown preference values are rejected before touching native state or notifying bindings.</summary>
    [Fact]
    public void Apply_InvalidLanguage_DoesNotAccessNativeState()
    {
        LocalizationService localization = new();
        int calls = 0;
        LanguageResourceCoordinator coordinator = new(localization, () => { calls++; return "en-US"; }, _ => calls++);

        Assert.Throws<ArgumentOutOfRangeException>(() => coordinator.Apply((AppLanguage)999));

        Assert.Equal(0, calls);
        Assert.Equal(AppLanguage.AutoDetect, localization.CurrentLanguage);
    }
}
