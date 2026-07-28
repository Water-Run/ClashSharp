extern alias ClashSharpUi;

using System.Reflection;
using AboutPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.AboutPageComposition;
using ConnectionsPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.ConnectionsPageComposition;
using LinksPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.LinksPageComposition;
using LogsPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.LogsPageComposition;
using ProfilesPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.ProfilesPageComposition;
using ProxiesPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.ProxiesPageComposition;
using RulesPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.RulesPageComposition;
using StatisticsPageComposition =
    ClashSharpUi::ClashSharp.Presentation.Composition.StatisticsPageComposition;

namespace ClashSharp.Tests.Unit.Presentation;

/// <summary>Verifies page composition contracts fail fast and remain immutable.</summary>
public sealed class SmallPageCompositionDependencyTests
{
    public static TheoryData<Type> DependencyTypes =>
    [
        typeof(AboutPageComposition.Dependencies),
        typeof(ConnectionsPageComposition.Dependencies),
        typeof(LinksPageComposition.Dependencies),
        typeof(LogsPageComposition.Dependencies),
        typeof(ProfilesPageComposition.Dependencies),
        typeof(ProxiesPageComposition.Dependencies),
        typeof(RulesPageComposition.Dependencies),
        typeof(StatisticsPageComposition.Dependencies),
    ];

    /// <summary>Verifies a page cannot be created with a missing required dependency graph.</summary>
    [Theory]
    [MemberData(nameof(DependencyTypes))]
    public void Constructor_RejectsMissingPrimaryDependency(Type dependencyType)
    {
        ConstructorInfo constructor = Assert.Single(dependencyType.GetConstructors());
        object?[] arguments = new object?[constructor.GetParameters().Length];

        TargetInvocationException exception = Assert.Throws<TargetInvocationException>(
            () => constructor.Invoke(arguments));

        Assert.IsType<ArgumentNullException>(exception.InnerException);
    }

    /// <summary>Verifies dependencies cannot be replaced after composition.</summary>
    [Theory]
    [MemberData(nameof(DependencyTypes))]
    public void Properties_AreReadOnly(Type dependencyType)
    {
        PropertyInfo[] properties = dependencyType.GetProperties(BindingFlags.Instance | BindingFlags.Public);

        Assert.NotEmpty(properties);
        Assert.All(properties, property => Assert.False(
            property.CanWrite,
            $"{dependencyType.Name}.{property.Name} must be immutable."));
    }
}
