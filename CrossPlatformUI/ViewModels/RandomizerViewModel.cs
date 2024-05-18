using System;
using System.Reactive;
using System.Reactive.Disposables;
using System.Text.Json.Serialization;
using CrossPlatformUI.Services;
using CrossPlatformUI.ViewModels.Tabs;
using DialogHostAvalonia;
using ReactiveUI;
using ReactiveUI.Validation.Extensions;
using ReactiveUI.Validation.Helpers;
using Microsoft.Extensions.DependencyInjection;
using RandomizerCore;

namespace CrossPlatformUI.ViewModels;

public class RandomizerViewModel : ReactiveValidationObject, IRoutableViewModel, IActivatableViewModel
{
    private bool IsFlagStringValid(string flags)
    {
        try
        {
            _ = new RandomizerConfiguration(flags);
            return true;
        }
        catch
        {
            return false;
        }
    }

    [JsonIgnore]
    public string Seed
    {
        get => Main.Config.Seed;
        set
        {
            Main.Config.Seed = value.Trim();
            this.RaisePropertyChanged();
        }
    }

    private int currentTabIndex;
    public int CurrentTabIndex { get => currentTabIndex; set => this.RaiseAndSetIfChanged(ref currentTabIndex, value); }
    
    [JsonConstructor]
    public RandomizerViewModel() {}
    public RandomizerViewModel(MainViewModel main)
    {
        Main = main;
        HostScreen = Main;
        CustomizeViewModel = new(Main);
        Activator = new ViewModelActivator();
        RerollSeed = ReactiveCommand.Create(() =>
        {
            Main.Config.Seed = new Random().Next(0, 999999999).ToString();
        });
        
        LoadPreset = ReactiveCommand.Create<string>((flags) =>
        {
            Main.Config.Flags = flags;
        });
        
        LoadRom = ReactiveCommand.CreateFromObservable(
            () => Main.Router.Navigate.Execute(Main.RomFileViewModel)
        );

        SaveFolder = ReactiveCommand.CreateFromTask(async () =>
        {
            var fileDialog = App.Current?.Services?.GetService<IFileDialogService>()!;
            var folder = await fileDialog.OpenFolderAsync();
            Main.OutputFilePath = folder?.Path.AbsolutePath ?? "";
        });
        CanGenerate = this.WhenAnyValue(
            x => x.Flags,
            x => x.Main.Config.Seed,
            x => x.Main.RomFileViewModel.HasRomData,
            (flags, seed, hasRomData) =>
                IsFlagStringValid(flags) && !string.IsNullOrWhiteSpace(seed) && hasRomData
        );
        // Generate = ReactiveCommand.CreateFromTask(async () =>
        // {
        //     await DialogHost.Show("GenerateRomDialog");
        // }, CanGenerate);
        this.WhenActivated(OnActivate);
    }

    private void OnActivate(CompositeDisposable disposable)
    {
        if (string.IsNullOrEmpty(Main.Config.Flags))
        {
            Flags = MainViewModel.BeginnerPreset;            
        }
        else
        {
            Flags = Main.Config.Flags;
        }

        Main.Config.PropertyChanged += (sender, args) =>
        {
            switch (args.PropertyName)
            {
                case "Flags":
                    Flags = ((RandomizerConfiguration)sender!).Flags;
                    break;
                case "Seed":
                    this.RaisePropertyChanged(nameof(Seed));
                    break;
            }
        };
        Main.RomFileViewModel.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case "HasRomData":
                    this.RaisePropertyChanged(nameof(CanGenerate));
                    break;
            }
        };
        var flagsValidation = this.WhenAnyValue(
            x => x.Flags,
            IsFlagStringValid
            );
        this.ValidationRule(
            x => x.Flags,
            flagsValidation,
            "Invalid Flags");
    }

    private string validatedFlags = "";

    [JsonIgnore]
    public string Flags
    {
        get => validatedFlags;
        set
        {
            if (IsFlagStringValid(value) && value != Main.Config.Flags)
            {
                Main.Config.Flags = value;
            }
            this.RaiseAndSetIfChanged(ref validatedFlags, value);
        }
    }

    [JsonIgnore]
    public bool IsDesktop { get; } = !OperatingSystem.IsBrowser();

    [JsonIgnore]
    public MainViewModel Main { get; }
    public CustomizeViewModel CustomizeViewModel { get; }
    
    [JsonIgnore]
    public ReactiveCommand<Unit, Unit> RerollSeed { get; }
    [JsonIgnore]
    public ReactiveCommand<Unit, Unit> Generate { get; }
    [JsonIgnore]
    public ReactiveCommand<Unit, Unit> SaveFolder { get; }
    [JsonIgnore]
    public ReactiveCommand<string, Unit> LoadPreset { get; }
    [JsonIgnore]
    public ReactiveCommand<Unit, IRoutableViewModel> LoadRom { get; }
    [JsonIgnore]
    public IObservable<bool> CanGenerate { get; private set; }

    // Unique identifier for the routable view model.
    [JsonIgnore]
    public string UrlPathSegment { get; } = Guid.NewGuid().ToString()[..5];
    [JsonIgnore]
    public IScreen HostScreen { get; }
    [JsonIgnore]
    public ViewModelActivator Activator { get; }
}
