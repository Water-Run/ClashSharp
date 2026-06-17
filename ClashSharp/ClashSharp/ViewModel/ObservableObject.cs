/*
 * Observable Object
 * Provides shared property change notification support for MVVM view models
 *
 * @author: WaterRun
 * @file: ViewModel/ObservableObject.cs
 * @date: 2026-06-17
 */

#nullable enable

using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace ClashSharp.ViewModel;

/// <summary>Base class for bindable view models that publish property changes.</summary>
/// <remarks>
/// Invariants: Notifications are raised only when stored values change according to the default equality comparer.
/// Thread safety: Not thread-safe; intended for UI-thread view model use.
/// Side effects: Raises <see cref="PropertyChanged"/> synchronously on the calling thread.
/// </remarks>
internal abstract class ObservableObject : INotifyPropertyChanged
{
    /// <summary>Occurs when a bindable property value changes.</summary>
    /// <remarks>
    /// The event fires synchronously on the caller's thread after the backing field has been updated.
    /// Reentrancy is possible when subscribers mutate the same view model; callers should keep handlers small.
    /// Subscribers must unsubscribe when their lifetime is shorter than the view model lifetime.
    /// </remarks>
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Updates a backing field and raises a property change notification when the value changes.</summary>
    /// <param name="storage">Backing field reference to update.</param>
    /// <param name="value">New value to assign; null is allowed when <typeparamref name="T"/> allows null.</param>
    /// <param name="propertyName">Bindable property name; supplied automatically by the compiler when omitted.</param>
    /// <typeparam name="T">Stored property value type.</typeparam>
    /// <returns>True when the backing field changed; otherwise false.</returns>
    protected bool SetProperty<T>(ref T storage, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(storage, value))
        {
            return false;
        }

        storage = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for the supplied property name.</summary>
    /// <param name="propertyName">Bindable property name; null means an unspecified property changed.</param>
    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
