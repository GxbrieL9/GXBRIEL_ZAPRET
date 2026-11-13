using System.ComponentModel;
using Xunit;
using GxbrielZapret.Mvvm;
using GxbrielZapret.Models;
using GxbrielZapret.Views;

namespace GxbrielZapret.Tests;

public class MvvmAndThemeTests
{
    private sealed class TestViewModel : ObservableObject
    {
        private string _title = string.Empty;
        private int _counter;

        public string Title
        {
            get => _title;
            set => SetField(ref _title, value);
        }

        public int Counter
        {
            get => _counter;
            set => SetField(ref _counter, value);
        }
    }

    [Fact]
    public void ObservableObject_RaisesPropertyChangedOnPropertyChange()
    {
        var vm = new TestViewModel();
        var changedProps = new List<string>();

        vm.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName != null)
                changedProps.Add(args.PropertyName);
        };

        vm.Title = "GXBRIEL_ZAPRET";
        vm.Counter = 42;

        Assert.Equal(2, changedProps.Count);
        Assert.Contains(nameof(TestViewModel.Title), changedProps);
        Assert.Contains(nameof(TestViewModel.Counter), changedProps);
    }

    [Fact]
    public void ObservableObject_DoesNotRaiseIfValueUnchanged()
    {
        var vm = new TestViewModel { Title = "Initial" };
        var changedProps = new List<string>();

        vm.PropertyChanged += (sender, args) =>
        {
            if (args.PropertyName != null)
                changedProps.Add(args.PropertyName);
        };

        vm.Title = "Initial"; // Same value

        Assert.Empty(changedProps);
    }

    [Fact]
    public void RelayCommand_ExecutesAndRespectsCanExecute()
    {
        bool executed = false;
        bool canExecute = true;

        var cmd = new RelayCommand(() => executed = true, () => canExecute);

        Assert.True(cmd.CanExecute(null));
        cmd.Execute(null);
        Assert.True(executed);

        canExecute = false;
        Assert.False(cmd.CanExecute(null));
    }

    [Fact]
    public void NotificationType_DetectsLevelsCorrectly()
    {
        Assert.Equal(NotificationType.Success, ToastNotifier.DetectNotificationType("GXBRIEL", "Обход успешно включён"));
        Assert.Equal(NotificationType.Error, ToastNotifier.DetectNotificationType("Ошибка", "Движок winws2 упал"));
        Assert.Equal(NotificationType.Warning, ToastNotifier.DetectNotificationType("Внимание", "Порт занят другим приложением"));
        Assert.Equal(NotificationType.ActionRequired, ToastNotifier.DetectNotificationType("Действие", "Требуется обновить настройки"));
        Assert.Equal(NotificationType.Info, ToastNotifier.DetectNotificationType("GXBRIEL", "Запущена фоновая проверка"));
    }

    [Fact]
    public void NotificationQueue_DeduplicatesIdenticalMessagesWithinWindow()
    {
        var queue = new NotificationQueue(TimeSpan.FromSeconds(2));
        var res1 = queue.Enqueue("Title", "Duplicate Message", out var t1, out var m1);
        Assert.Equal(NotificationQueue.PostAction.ShowImmediately, res1);

        // Immediate second identical message should be dropped
        var res2 = queue.Enqueue("Title", "Duplicate Message", out var t2, out var m2);
        Assert.Equal(NotificationQueue.PostAction.DropDuplicate, res2);

        // Different message should be shown
        var res3 = queue.Enqueue("Title", "Another Unique Message", out var t3, out var m3);
        Assert.Equal(NotificationQueue.PostAction.ShowImmediately, res3);
    }

    [Fact]
    public void NotificationQueue_GroupsBurstEvents()
    {
        var queue = new NotificationQueue(TimeSpan.FromSeconds(1));
        var actions = new List<NotificationQueue.PostAction>();

        for (int i = 0; i < 6; i++)
        {
            var res = queue.Enqueue($"Target {i}", $"Discovered endpoint {i}", out var t, out var m);
            actions.Add(res);
        }

        // First 3 should be immediate, subsequent within 600ms should be grouped
        Assert.Equal(NotificationQueue.PostAction.ShowImmediately, actions[0]);
        Assert.Equal(NotificationQueue.PostAction.ShowImmediately, actions[1]);
        Assert.Equal(NotificationQueue.PostAction.ShowImmediately, actions[2]);
        Assert.Equal(NotificationQueue.PostAction.GroupBurst, actions[3]);
        Assert.Equal(NotificationQueue.PostAction.GroupBurst, actions[4]);
        Assert.Equal(NotificationQueue.PostAction.GroupBurst, actions[5]);
    }
}
