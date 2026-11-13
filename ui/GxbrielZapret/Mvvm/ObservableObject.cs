using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;

namespace GxbrielZapret.Mvvm;

public abstract class ObservableObject : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    protected bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        return SetProperty(ref field, value, name);
    }
}
