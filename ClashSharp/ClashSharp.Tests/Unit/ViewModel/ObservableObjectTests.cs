/*
 * Observable Object Tests
 * Verifies shared MVVM property notification behavior
 *
 * @author: WaterRun
 * @file: ClashSharp.Tests/Unit/ViewModel/ObservableObjectTests.cs
 * @date: 2026-06-17
 */

using ClashSharp.ViewModel;

namespace ClashSharp.Tests.Unit.ViewModel;

/// <summary>Unit tests for the shared observable object base class.</summary>
public sealed class ObservableObjectTests
{
    /// <summary>Verifies changed values raise exactly one property change notification.</summary>
    [Fact]
    public void SetProperty_WhenValueChanges_RaisesPropertyChanged()
    {
        TestObservableObject viewModel = new();
        List<string?> changedProperties = [];
        viewModel.PropertyChanged += (_, args) => changedProperties.Add(args.PropertyName);

        bool changed = viewModel.SetName("Clash#");

        Assert.True(changed);
        Assert.Equal("Clash#", viewModel.Name);
        Assert.Equal(["Name"], changedProperties);
    }

    /// <summary>Verifies equal values are ignored and do not raise notifications.</summary>
    [Fact]
    public void SetProperty_WhenValueIsEqual_DoesNotRaisePropertyChanged()
    {
        TestObservableObject viewModel = new();
        int notificationCount = 0;
        viewModel.PropertyChanged += (_, _) => notificationCount++;

        bool changed = viewModel.SetName("Initial");

        Assert.False(changed);
        Assert.Equal("Initial", viewModel.Name);
        Assert.Equal(0, notificationCount);
    }

    /// <summary>Test view model exposing the protected set helper.</summary>
    private sealed class TestObservableObject : ObservableObject
    {
        /// <summary>Backing field for <see cref="Name"/>.</summary>
        private string _name = "Initial";

        /// <summary>Gets the observable test name.</summary>
        /// <value>The current test name.</value>
        public string Name => _name;

        /// <summary>Sets the test name through the observable helper.</summary>
        /// <param name="name">Name value to assign. Must not be null.</param>
        /// <returns>True when the value changed; otherwise false.</returns>
        public bool SetName(string name)
        {
            return SetProperty(ref _name, name, nameof(Name));
        }
    }
}
