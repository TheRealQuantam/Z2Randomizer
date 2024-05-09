﻿using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Runtime.Serialization;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Contexts;
using ReactiveUI.Validation.Helpers;
using Z2Randomizer.Core;

namespace CrossPlatformUI.ViewModels;

[DataContract]
public class MainViewModel : ReactiveValidationObject, IScreen, IActivatableViewModel
{
    public RandomizerConfiguration Config { get; } = new();

    private bool canLoadRom;
    public bool CanLoadRom { get => canLoadRom; set => this.RaiseAndSetIfChanged(ref canLoadRom, value); }

    // The Router associated with this Screen.
    // Required by the IScreen interface.
    public RoutingState Router { get; } = new ();

    // The command that navigates a user to first view model.
    public ReactiveCommand<Unit, IRoutableViewModel> LoadRom { get; }
    public ReactiveCommand<Unit, IRoutableViewModel> GenerateRom { get; }

    // The command that navigates a user back.
    public ReactiveCommand<Unit, IRoutableViewModel> GoBack => Router.NavigateBack!;

    [DataMember]
    public RomFileViewModel RomFileViewModel { get; }
    [DataMember]
    public RandomizerViewModel RandomizerViewModel { get; }
    public GenerateRomViewModel GenerateRomViewModel { get; }

    
    public MainViewModel()
    {
        Router.CurrentViewModel.Subscribe(view =>
        {
            CanLoadRom = view != RomFileViewModel;
        });
        RomFileViewModel = new(this);
        GenerateRomViewModel = new(this);
        RandomizerViewModel = new(this);
        Router.Navigate.Execute(RandomizerViewModel);

        LoadRom = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute(RomFileViewModel)
        );
        GenerateRom = ReactiveCommand.CreateFromObservable(
            () => Router.Navigate.Execute()
        );
        
        this.WhenActivated(ShowRomFileViewIfNoRom);
        return;
        void ShowRomFileViewIfNoRom(CompositeDisposable disposables)
        {
            if (!RomFileViewModel.HasRomData)
            {
                Router.Navigate.Execute(RomFileViewModel);
            }
            Disposable.Create(() => { })
                .DisposeWith(disposables);
        }
    }
    
    // Unique identifier for the routable view model.
    public string UrlPathSegment { get; } = Guid.NewGuid().ToString()[..5];
    public ViewModelActivator Activator { get; } = new ();
}