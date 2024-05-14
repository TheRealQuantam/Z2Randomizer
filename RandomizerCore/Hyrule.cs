﻿using NLog;
using RandomizerCore;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RandomizerCore.Asm;
using Z2Randomizer.Core.Overworld;
using Z2Randomizer.Core.Sidescroll;

namespace Z2Randomizer.Core;

public class Hyrule
{
    //IMPORTANT: Tuning these factors can have a big impact on generation times.
    //In general, the non-terrain shuffle features are much cheaper than the cost of generating terrain or verifying the seed.

    //This controls how many attempts will be made to shuffle the non-terrain features like towns, spells, and items.
    //The higher you set it, the more likely a given terrain is to find a set of items that works, resulting in fewer terrain generations.
    //It will also increase the number of seeds that have more arcane solutions, where only a specific item route works.
    //This was originally set to 10, but increasing it to 100 massively reduces the number of extremely degenerate caldera and mountain generation times
    private const int NON_TERRAIN_SHUFFLE_ATTEMPT_LIMIT = 500;

    //This controls how many times 
    private const int NON_CONTINENT_SHUFFLE_ATTEMPT_LIMIT = 10;

    public const bool UNSAFE_DEBUG = false;

    //private readonly Item[] SHUFFLABLE_STARTING_ITEMS = new Item[] { Item.CANDLE, Item.GLOVE, Item.RAFT, Item.BOOTS, Item.FLUTE, Item.CROSS, Item.HAMMER, Item.MAGIC_KEY };

    private static readonly Logger logger = LogManager.GetCurrentClassLogger();


    private readonly int[] palPalettes = { 0, 0x00, 0x10, 0x20, 0x30, 0x40, 0x50, 0x60 };
    private readonly int[] palGraphics = { 0, 0x04, 0x05, 0x09, 0x0A, 0x0B, 0x0C, 0x06 };

    private static readonly Collectable[] SHUFFLABLE_STARTING_ITEMS = [
        Collectable.CANDLE, Collectable.GLOVE,
        Collectable.RAFT,
        Collectable.BOOTS,
        Collectable.FLUTE,
        Collectable.CROSS,
        Collectable.HAMMER,
        Collectable.MAGIC_KEY
    ];


    //private ROM romData;
    private const int overworldXOff = 0x3F;
    private const int overworldMapOff = 0x7E;
    private const int overworldWorldOff = 0xBD;

    private static readonly List<Town> spellTowns = new List<Town>() { Town.RAURU, Town.RUTO, Town.SARIA_NORTH, Town.MIDO_WEST,
        Town.MIDO_CHURCH, Town.NABOORU, Town.DARUNIA_WEST, Town.DARUNIA_ROOF, Town.NEW_KASUTO, Town.OLD_KASUTO};

    //Unused
    //private Dictionary<int, int> spellEnters;
    //Unused
    //private Dictionary<int, int> spellExits;
    //Unused
    public HashSet<String> reachableAreas;
    //Which vanilla spell corresponds with which shuffled spell
    //private Dictionary<Town, Collectable> WizardCollectables { get; set; }
    //Locations that contain an item
    private List<Location> itemLocs;
    //Locations that are pbags in vanilla that are turned into hearts because maxhearts - startinghearts > 4
    private List<Location> pbagHearts;
    //Continent connectors
    protected (Location, Location)[] connections = new (Location, Location)[4];

    //public SortedDictionary<String, List<Location>> areasByLocation;
    //public Dictionary<Location, String> section;
    private int accessibleMagicContainers;
    private int heartContainers;
    private int startHearts;
    private int maxHearts;
    private int heartContainersInItemPool;
    private int kasutoJars;

    //private Character character;

    public Dictionary<Collectable, bool> ItemGet { get; set; }
    //private bool[] spellGet;

    public WestHyrule westHyrule;
    public EastHyrule eastHyrule;
    private MazeIsland mazeIsland;
    private DeathMountain deathMountain;

    private Shuffler shuffler;
    private RandomizerProperties props;
    public List<World> worlds;
    public List<Palace> palaces;
    public List<Room> rooms;

    //DEBUG/STATS
    private static int DEBUG_THRESHOLD = 130;
    public DateTime startTime = DateTime.Now;
    public DateTime startRandomizeStartingValuesTimestamp;
    public DateTime startRandomizeEnemiesTimestamp;
    public DateTime firstProcessOverworldTimestamp;

    public int totalReachabilityOverworldAttempts = 0;
    public int totalContinentConnectionOverworldAttempts = 0;
    public int totalWestGenerationAttempts = 0;
    public int totalEastGenerationAttempts = 0;
    public int totalMazeIslandGenerationAttempts = 0;
    public int totalDeathMountainGenerationAttempts = 0;
    public int isEverythingReachableFailures = 0;

    public int itemGetReachableFailures = 0;
    public int spellGetReachableFailures = 0;
    public int heartContainerReachableFailures = 0;
    public int magicContainerReachableFailures = 0;
    public int logicallyRequiredLocationsReachableFailures = 0;

    public int timeSpentBuildingWest = 0;
    public int timeSpentBuildingEast = 0;
    public int timeSpentBuildingDM = 0;
    public int timeSpentBuildingMI = 0;

    public int debug = 0;
    public int totalReachableCheck = 0;


    private readonly SortedDictionary<int, int> palaceConnectionLocs = new SortedDictionary<int, int>
    {
        {1, 0x1072B},
        {2, 0x1072B},
        {3, 0x12208},
        {4, 0x12208},
        {5, 0x1072B},
        {6, 0x12208},
        {7, 0x1472B},
    };

    private readonly Dictionary<int, int> palaceAddr = new Dictionary<int, int>
    {
        {1, 0x4663 },
        {2, 0x4664 },
        {3, 0x4665 },
        {4, 0xA140 },
        {5, 0x8663 },
        {6, 0x8664 },
        {7, 0x8665 }
    };

    public ROM ROMData { get; set; }
    public Random RNG { get; set; }
    public string Flags { get; private set; }
    public int Seed { get; private set; }
    public RandomizerProperties Props
    {
        get
        {
            return props;
        }

        set
        {
            props = value;
        }
    }

    private List<Text> hints;
    private readonly IAsmEngine engine;
    private readonly PalaceRooms palaceRooms;

    public Hyrule(IAsmEngine engine, PalaceRooms rooms)
    {
        this.engine = engine;
        palaceRooms = rooms;
    }
    public async Task<byte[]?> Randomize(byte[] vanillaRomData, RandomizerConfiguration config, Action<string> progress, CancellationToken ct)
    {
        World.ResetStats();
        Seed = BitConverter.ToInt32(MD5Hash.ComputeHash(Encoding.UTF8.GetBytes(config.Seed)).AsSpan()[..4]);
        RNG = new Random(Seed);
        props = config.Export(RNG);
        if(UNSAFE_DEBUG) 
        {
            string export = JsonSerializer.Serialize(props, SourceGenerationContext.Default.RandomizerProperties);
            Debug.WriteLine(export);
        }
        Flags = config.Flags;
        
        Assembler assembler = new();
        logger.Info("Started generation for " + Flags + " / " + Seed);
        //character = new Character(props);
        shuffler = new Shuffler(props);

        palaces = [];
        ItemGet = [];
        foreach (Collectable item in Enum.GetValues(typeof(Collectable)))
        {
            if(item.IsMajorItem())
            {
                ItemGet.Add(item, false);
            }
        }
        ItemGet.Remove(props.ReplaceFireWithDash ? Collectable.FIRE_SPELL : Collectable.DASH_SPELL);
        accessibleMagicContainers = 4;
        reachableAreas = new HashSet<string>();
        //areasByLocation = new SortedDictionary<string, List<Location>>();

        ROMData = new ROM(vanillaRomData);
        hints = ROMData.GetGameText();

        if(props.DisableMagicRecs)
        {
            kasutoJars = 1;
        }
        else if (props.KasutoJars)
        {
            kasutoJars = RNG.Next(5, 8);
        }
        else
        {
            kasutoJars = 7;
        }

        bool raftIsRequired = IsRaftAlwaysRequired(props);
        bool passedValidation = false;
        while (palaces.Count != 7 || passedValidation == false)
        {
            palaces = Palaces.CreatePalaces(ct, RNG, props, palaceRooms, raftIsRequired);
            if(palaces.Count == 0)
            {
                continue;
            }

            //Randomize Enemies
            if (props.ShufflePalaceEnemies)
            {
                palaces.ForEach(i => i.RandomizeEnemies(props, RNG));
            }

            if (props.ShuffleSmallItems || props.ExtraKeys)
            {
                palaces[0].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[1].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[2].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[3].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[4].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[5].RandomizeSmallItems(RNG, props.ExtraKeys);
                palaces[6].RandomizeSmallItems(RNG, props.ExtraKeys);
            }

            AsmModule sideview_module = new();
            AsmModule gp_sideview_module = new();
            //AssemblerCommon.AssemblerCommon validation_sideview_module = new();
            //AssemblerCommon.AssemblerCommon validation_gp_sideview_module = new();

            //This is an awful hack. We need to make a determination about whether the sideviews can fit in the available space,
            //but there is (at present) no way to test whether that is possible without rendering the entire engine into an irrecoverably
            //broken state, so we'll just run it twice. As long as this is the first modification that gets made on the engine, this is
            //guaranteed to succeed iff running on the original engine would succeed.
            //Jrowe feel free to engineer a less insane fix here. 
            Assembler validationEngine = new();

            int i = 0;
            //In Reconstructed, enemy pointers aren't separated between 125 and 346, they're just all in 1 big pile,
            //so we just start at the 125 pointer address
            int enemyAddr = Enemies.NormalPalaceEnemyAddr;
            Dictionary<byte[], List<Room>> sideviews = new(new Util.StandardByteArrayEqualityComparer());
            Dictionary<byte[], List<Room>> sideviewsgp = new(new Util.StandardByteArrayEqualityComparer());
            foreach (Room room in palaces.Where(i => i.Number < 7).SelectMany(i => i.AllRooms))
            {
                if (sideviews.ContainsKey(room.SideView))
                {
                    sideviews[room.SideView].Add(room);
                }
                else
                {
                    List<Room> l = new List<Room> { room };
                    sideviews.Add(room.SideView, l);
                }
            }
            foreach (Room room in palaces.Where(i => i.Number == 7).SelectMany(i => i.AllRooms))
            {
                if (sideviewsgp.ContainsKey(room.SideView))
                {
                    sideviewsgp[room.SideView].Add(room);
                }
                else
                {
                    List<Room> l = new List<Room> { room };
                    sideviewsgp.Add(room.SideView, l);
                }
            }

            foreach (byte[] sv in sideviews.Keys)
            {
                var name = "Sideview_" + i++;
                sideview_module.Segment("PRG4", "PRG7");
                sideview_module.Reloc();
                sideview_module.Label(name);
                sideview_module.Byt(sv);
                List<Room> rooms = sideviews[sv];
                foreach (Room room in rooms)
                {
                    room.WriteSideViewPtr(sideview_module, name);
                    room.UpdateItemGetBits(ROMData);
                    room.UpdateEnemies(enemyAddr, ROMData);
                    enemyAddr += room.NewEnemies.Length;
                    room.UpdateConnectionBytes();
                    room.UpdateConnectionStartAddress();
                }
            }

            i = 0;
            //GP Reconstructed
            enemyAddr = Enemies.GPEnemyAddr;
            foreach (byte[] sv in sideviewsgp.Keys)
            {
                var name = "SideviewGP_" + i++;

                gp_sideview_module.Segment("PRG5", "PRG7");
                gp_sideview_module.Reloc();
                gp_sideview_module.Label(name);
                gp_sideview_module.Byt(sv);
                List<Room> rooms = sideviewsgp[sv];
                foreach (Room room in rooms)
                {
                    room.WriteSideViewPtr(gp_sideview_module, name);
                    room.UpdateItemGetBits(ROMData);
                    room.UpdateEnemies(enemyAddr, ROMData);
                    enemyAddr += room.NewEnemies.Length;
                    room.UpdateConnectionBytes();
                    room.UpdateConnectionStartAddress();
                }
            }

            try
            {
                validationEngine.Add(sideview_module);
                validationEngine.Add(gp_sideview_module);
                ROM testRom = new(ROMData);
                ApplyAsmPatches(props, validationEngine, RNG, testRom);
                await testRom.ApplyAsm(engine, validationEngine); //.Wait(ct);
            }
            catch (Exception e)
            {
                logger.Debug(e, "Room packing failed. Retrying.");
                continue;
            }
            passedValidation = true;
            assembler.Add(sideview_module);
            assembler.Add(gp_sideview_module);
        }

        //Allows casting magic without requeueing a spell
        if (props.FastCast)
        {
            ROMData.WriteFastCastMagic();
        }

        if (props.DisableMusic)
        {
            ROMData.DisableMusic();
        }

        ROMData.WriteKasutoJarAmount(kasutoJars);
        ROMData.DoHackyFixes();
        shuffler.ShuffleDrops(ROMData, RNG);
        shuffler.ShufflePbagAmounts(ROMData, RNG);

        ROMData.DisableTurningPalacesToStone();
        ROMData.UpdateMapPointers();

        if (props.DashAlwaysOn)
        {
            ROMData.Put(0x13C3, new byte[] { 0x30, 0xD0 });
        }

        if (props.PermanentBeam)
        {
            ROMData.Put(0x186c, 0xEA);
            ROMData.Put(0x186d, 0xEA);
        }

        ShortenWizards();

        // startRandomizeStartingValuesTimestamp = DateTime.Now;
        startRandomizeEnemiesTimestamp = DateTime.Now;
        RandomizeEnemyStats();

        firstProcessOverworldTimestamp = DateTime.Now;
        ProcessOverworld(progress, ct);
        bool f = UpdateProgress(progress, ct, 8);
        if (!f)
        {
            return null;
        }
        //XXX: Is this being handled elsewhere?
        /*
        if (props.CombineFire)
        {
            LinkFire();
        }
        */

        //If you start with a spell, also start with its corresponding spell item if applicable.
        //This is specifically only true if the collectable behind the wizard is a spell and spells are not in the general pool
        //if that's the case, you already know the check is clear just from looking at the menu.
        //If it's not the case, you have no idea what that wizard has, so it's a live check.
        /*
        if (!props.IncludeSpellsInShuffle)
        {
            if (props.StartWithCollectable(eastHyrule.AllLocations.First(i => i.ActualTown == Town.NABOORU).Collectable))
            {
                ROMData.Put(0x17b17, 0x01); //Water
            }
            if (props.StartWithCollectable(eastHyrule.AllLocations.First(i => i.ActualTown == Town.DARUNIA_WEST).Collectable))
            {
                ROMData.Put(0x17b18, 0x20); //Child
            }
        }
        */

        Dictionary<Town, Collectable> spellMap = new()
        {
            { westHyrule.locationAtRauru.ActualTown, westHyrule.locationAtRauru.Collectable },
            { westHyrule.locationAtMido.ActualTown, westHyrule.locationAtMido.Collectable },
            { westHyrule.locationAtSariaNorth.ActualTown, westHyrule.locationAtSariaNorth.Collectable },
            { westHyrule.locationAtRuto.ActualTown, westHyrule.locationAtRuto.Collectable },
            { eastHyrule.townAtNabooru.ActualTown, eastHyrule.townAtNabooru.Collectable },
            { eastHyrule.townAtDarunia.ActualTown, eastHyrule.townAtDarunia.Collectable },
            { eastHyrule.townAtNewKasuto.ActualTown, eastHyrule.townAtNewKasuto.Collectable },
            { eastHyrule.townAtOldKasuto.ActualTown, eastHyrule.townAtOldKasuto.Collectable },
        };
        hints = CustomTexts.GenerateTexts(itemLocs, spellMap, westHyrule.bagu, hints, props, RNG);
        f = UpdateProgress(progress, ct, 9);
        if (!f)
        {
            return null;
        }

        ApplyAsmPatches(props, assembler, RNG, ROMData);
        var rom = await ROMData.ApplyAsm(engine, assembler);

        // await assemblerTask; // .Wait(ct);
        // var rom = assemblerTask.Result;
        ROMData = new ROM(rom!);

        byte[] finalRNGState = new byte[32];
        RNG.NextBytes(finalRNGState);
        var version = Assembly.GetEntryAssembly().GetName().Version;
        var versionstr = $"{version.Major}.{version.Minor}.{version.Build}";
        byte[] hash = MD5Hash.ComputeHash(Encoding.UTF8.GetBytes(
            Flags +
            Seed +
            versionstr +
            // TODO get room file hash
            // Util.ReadAllTextFromFile(config.GetRoomsFile()) +
            Util.ByteArrayToHexString(finalRNGState)
        ));
        UpdateRom(hash);

        if (UNSAFE_DEBUG)
        {
            PrintSpoiler(LogLevel.Error);
            //DEBUG
            StringBuilder sb = new();
            foreach (Palace palace in palaces)
            {
                sb.AppendLine("Palace: " + palace.Number);
                foreach (Room room in palace.AllRooms.OrderBy(i => i.Map))
                {
                    sb.AppendLine(room.Debug());
                }
                File.WriteAllText("rooms.log", sb.ToString());
            }
        }
        return ROMData.rawdata;
    }


    /*
        Text Notes:
        
        Community Text Changes
        ----------------------
        Shield Spell    15  43
        Cannot Help     16  35
        Jump Spell      24  34
        Life Spell      35  37
        You know..?     37  42
        Fairy           46  37
        Downstab        47  38
        Bagu            48  44
        Fire            70  43
        You know        71  34
        Reflect         81  37
        Upstab          82  32
        Spell           93  25
        Thunder         96  36 
    */


    private void RandomizeAttackEffectiveness(ROM rom, StatEffectiveness attackEffectiveness)
    {
        if (attackEffectiveness == StatEffectiveness.MAX)
        {
            for (int i = 0; i < 8; i++)
            {
                ROMData.Put(0x1E67D + i, 192);
            }
            return;
        }
        if(attackEffectiveness == StatEffectiveness.VANILLA)
        {
            return;
        }
        int[] attackValues = new int[8];
        for (int i = 0; i < 8; i++)
        {
            attackValues[i] = rom.GetByte(0x1E67D + i);
        }

        int[] newAttackValues = new int[8];
        for (int i = 0; i < 8; i++)
        {
            double minAtk = attackValues[i] - attackValues[i] * .333;
            double maxAtk = attackValues[i] + attackValues[i] * .5;

            double attack;
            if (attackEffectiveness == StatEffectiveness.AVERAGE)
            {
                attack = RNG.NextDouble() * (maxAtk - minAtk) + minAtk;
                if (i == 0)
                {
                    attack = (int)Math.Round(Math.Max(attack, 2));
                }
                else
                {
                    if (attack < newAttackValues[i - 1])
                    {
                        attack = newAttackValues[i - 1];
                    }
                    else
                    {
                        attack = (int)Math.Round(attack);
                    }
                }
                attack = (int)Math.Min(attack, maxAtk);
                attack = (int)Math.Max(attack, minAtk);
            }
            else if (attackEffectiveness == StatEffectiveness.HIGH)
            {
                attack = (int)(attackValues[i] + (attackValues[i] * .5));
            }
            else if (attackEffectiveness == StatEffectiveness.LOW)
            {
                //Low attack does really dumb stuff with rounding regardless of what you do because the values are so low
                //This causes at least 1 level to to literal nothing. To avoid this, we just have a linear increase from 1-6
                //Meeting up at 6 where the curve would be anyway.
                if (i <= 6)
                {
                    attack = i;
                }
                else
                {
                    attack = (int)Math.Round(attackValues[i] - (attackValues[i] * .5), MidpointRounding.ToPositiveInfinity);
                }
            }
            else
            {
                throw new Exception("Invalid Attack Effectiveness");
            }

            newAttackValues[i] = (int)attack;
        }


        for (int i = 0; i < 8; i++)
        {
            rom.Put(0x1E67D + i, (byte)newAttackValues[i]);
        }
    }

    private void ShuffleItems()
    {
        List<Collectable> shufflableItems = [
            Collectable.CANDLE, Collectable.GLOVE, Collectable.RAFT, Collectable.BOOTS, 
            Collectable.FLUTE, Collectable.CROSS, Collectable.HEART_CONTAINER, Collectable.HEART_CONTAINER, 
            Collectable.MAGIC_CONTAINER, Collectable.MEDICINE, Collectable.TROPHY, Collectable.HEART_CONTAINER, 
            Collectable.HEART_CONTAINER, Collectable.MAGIC_CONTAINER, Collectable.MAGIC_KEY, Collectable.MAGIC_CONTAINER, 
            Collectable.HAMMER, Collectable.CHILD, Collectable.MAGIC_CONTAINER];
        List<Collectable> smallItems = [Collectable.BLUE_JAR, Collectable.RED_JAR, Collectable.SMALL_BAG, 
            Collectable.MEDIUM_BAG, Collectable.LARGE_BAG, Collectable.XL_BAG, Collectable.ONEUP, Collectable.KEY];

        if (props.PbagItemShuffle)
        {
            westHyrule.pbagCave.Collectable = (Collectable)ROMData.GetByte(0x4FE2);
            eastHyrule.pbagCave1.Collectable = (Collectable)ROMData.GetByte(0x8ECC);
            eastHyrule.pbagCave2.Collectable = (Collectable)ROMData.GetByte(0x8FB3);
            shufflableItems.Add(westHyrule.pbagCave.Collectable);
            shufflableItems.Add(eastHyrule.pbagCave1.Collectable);
            shufflableItems.Add(eastHyrule.pbagCave2.Collectable);
        }

        if(UNSAFE_DEBUG && props.IncludeSpellsInShuffle)
        {
            shufflableItems.Add(Collectable.SHIELD_SPELL);
            shufflableItems.Add(Collectable.JUMP_SPELL);
            shufflableItems.Add(Collectable.LIFE_SPELL);
            shufflableItems.Add(Collectable.FAIRY_SPELL);
            shufflableItems.Add(props.ReplaceFireWithDash ? Collectable.DASH_SPELL : Collectable.FIRE_SPELL);
            shufflableItems.Add(Collectable.REFLECT_SPELL);
            shufflableItems.Add(Collectable.SPELL_SPELL);
            shufflableItems.Add(Collectable.THUNDER_SPELL);
        }
        else if(props.ShuffleSpellLocations)
        {
            ShuffleSpells();
        }

        if (UNSAFE_DEBUG && props.IncludeQuestItemsInShuffle)
        {
            shufflableItems.Add(Collectable.BAGUS_NOTE);
            shufflableItems.Add(Collectable.MIRROR);
            shufflableItems.Add(Collectable.WATER);
        }

        if (props.IncludeSwordTechsInShuffle)
        {
            shufflableItems.Add(Collectable.UPSTAB);
            shufflableItems.Add(Collectable.DOWNSTAB);
        }
        else if(props.SwapUpAndDownStab)
        {
            HandleSwapUpAndDownstab();
        }

        heartContainersInItemPool = maxHearts - startHearts;

        foreach (Collectable item in ItemGet.Keys.ToList())
        {
            ItemGet[item] = false;
        }
        foreach (Location? location in itemLocs)
        {
            location.ItemGet = false;
        }
        westHyrule.pbagCave.ItemGet = false;
        eastHyrule.pbagCave1.ItemGet = false;
        eastHyrule.pbagCave2.ItemGet = false;

        //TODO: Refactor these puts out of this class
        ROMData.Put(RomMap.START_CANDLE, props.StartCandle ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_GLOVE, props.StartGlove ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_RAFT, props.StartRaft ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_BOOTS, props.StartBoots ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_FLUTE, props.StartFlute ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_CROSS, props.StartCross ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_HAMMER, props.StartHammer ? (byte)1 : (byte)0);
        ROMData.Put(RomMap.START_MAGICAL_KEY, props.StartKey ? (byte)1 : (byte)0);

        ItemGet[Collectable.CANDLE] = props.StartCandle;
        ItemGet[Collectable.GLOVE] = props.StartGlove;
        ItemGet[Collectable.RAFT] = props.StartRaft;
        ItemGet[Collectable.BOOTS] = props.StartBoots;
        ItemGet[Collectable.FLUTE] = props.StartFlute;
        ItemGet[Collectable.CROSS] = props.StartCross;
        ItemGet[Collectable.HAMMER] = props.StartHammer;
        ItemGet[Collectable.MAGIC_KEY] = props.StartKey;

        pbagHearts = new List<Location>();
        //Replace any unused heart containers with small items
        if (heartContainersInItemPool < 4)
        {
            int heartContainersToAdd = 4 - heartContainersInItemPool;
            while (heartContainersToAdd > 0)
            {
                int remove = RNG.Next(shufflableItems.Count);
                if (shufflableItems[remove] == Collectable.HEART_CONTAINER)
                {
                    shufflableItems[remove] = smallItems[RNG.Next(smallItems.Count)];
                    heartContainersToAdd--;
                }
            }
        }

        //For heart containers beyond 4, replace small items in the pool as available.
        if (heartContainersInItemPool > 4)
        {
            if (props.PbagItemShuffle)
            {
                int heartContainersToAdd = heartContainersInItemPool - 4;
                while (heartContainersToAdd > 0)
                {
                    shufflableItems[22 - heartContainersToAdd] = Collectable.HEART_CONTAINER;
                    heartContainersToAdd--;
                }
            }
            else
            {
                int x = heartContainersInItemPool - 4;
                while (x > 0)
                {
                    int y = RNG.Next(3);
                    if (y == 0 && !pbagHearts.Contains(westHyrule.pbagCave))
                    {
                        pbagHearts.Add(westHyrule.pbagCave);
                        westHyrule.pbagCave.Collectable = Collectable.HEART_CONTAINER;
                        shufflableItems.Add(Collectable.HEART_CONTAINER);
                        itemLocs.Add(westHyrule.pbagCave);
                        x--;
                    }
                    if (y == 1 && !pbagHearts.Contains(eastHyrule.pbagCave1))
                    {
                        pbagHearts.Add(eastHyrule.pbagCave1);
                        eastHyrule.pbagCave1.Collectable = Collectable.HEART_CONTAINER;
                        shufflableItems.Add(Collectable.HEART_CONTAINER);
                        itemLocs.Add(eastHyrule.pbagCave1);
                        x--;
                    }
                    if (y == 2 && !pbagHearts.Contains(eastHyrule.pbagCave2))
                    {
                        pbagHearts.Add(eastHyrule.pbagCave2);
                        eastHyrule.pbagCave2.Collectable = Collectable.HEART_CONTAINER;
                        shufflableItems.Add(Collectable.HEART_CONTAINER);
                        itemLocs.Add(eastHyrule.pbagCave2);
                        x--;
                    }
                }
            }
        }

        if (props.StartWithSpellItems)
        {
            shufflableItems[9] = smallItems[RNG.Next(smallItems.Count)];
            shufflableItems[10] = smallItems[RNG.Next(smallItems.Count)];
            shufflableItems[17] = smallItems[RNG.Next(smallItems.Count)];
            ItemGet[Collectable.TROPHY] = true;
            ItemGet[Collectable.MEDICINE] = true;
            ItemGet[Collectable.CHILD] = true;
        }

        if (ItemGet[westHyrule.AllLocations.First(i => i.ActualTown == Town.RUTO).Collectable])
        {
            shufflableItems[10] = smallItems[RNG.Next(smallItems.Count)];
            ItemGet[Collectable.TROPHY] = true;
        }

        if (ItemGet[westHyrule.AllLocations.First(i => i.ActualTown == Town.MIDO_WEST).Collectable])
        {
            shufflableItems[9] = smallItems[RNG.Next(smallItems.Count)];
            ItemGet[Collectable.MEDICINE] = true;
        }

        if (ItemGet[eastHyrule.AllLocations.First(i => i.ActualTown == Town.DARUNIA_WEST).Collectable])
        {
            shufflableItems[17] = smallItems[RNG.Next(smallItems.Count)];
            ItemGet[Collectable.CHILD] = true;
        }

        //TODO: Clean up the readability of this logic
        if (ItemGet[Collectable.CANDLE])
        {
            shufflableItems[0] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.GLOVE])
        {
            shufflableItems[1] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.RAFT])
        {
            shufflableItems[2] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.BOOTS])
        {
            shufflableItems[3] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.FLUTE])
        {
            shufflableItems[4] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.CROSS])
        {
            shufflableItems[5] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.MAGIC_KEY])
        {
            shufflableItems[14] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (ItemGet[Collectable.HAMMER])
        {
            shufflableItems[16] = smallItems[RNG.Next(smallItems.Count)];
        }

        if (props.MixOverworldPalaceItems)
        {
            /*
            for (int i = itemList.Count - 1; i > 0; i--)
            {
                int s = RNG.Next(i, itemList.Count);
                (itemList[i], itemList[s]) = (itemList[s], itemList[i]);
            }*/
            shufflableItems.FisherYatesShuffle(RNG);
        }
        else
        {
            if (props.ShufflePalaceItems)
            {
                for (int i = 5; i > 0; i--)
                {
                    int s = RNG.Next(i + 1);
                    (shufflableItems[i], shufflableItems[s]) = (shufflableItems[s], shufflableItems[i]);
                }
            }

            if (props.ShuffleOverworldItems)
            {
                for (int i = shufflableItems.Count - 1; i > 6; i--)
                {
                    int s = RNG.Next(6, i + 1);
                    (shufflableItems[i], shufflableItems[s]) = (shufflableItems[s], shufflableItems[i]);
                }
            }
        }
        if(shufflableItems.Count != itemLocs.Count)
        {
            throw new Exception("Item locations must match number of items");
        }

        for (int i = 0; i < shufflableItems.Count; i++)
        {
            itemLocs[i].Collectable = shufflableItems[i];
        }
    }

    private bool IsEverythingReachable(Dictionary<Collectable, bool> itemGet)
    {
        totalReachableCheck++;
        int dm = 0;
        int mi = 0;
        int wh = 0;
        int eh = 0;
        int count = 1;
        int prevCount = 0;
        int loopCount = 0;
        debug++;

        int totalLocationsCount = westHyrule.AllLocations.Count + eastHyrule.AllLocations.Count 
            + deathMountain.AllLocations.Count + mazeIsland.AllLocations.Count;
        //logger.Debug("Locations count: West-" + westHyrule.AllLocations.Count + " East-" + eastHyrule.AllLocations.Count +
        //   " DM-" + deathMountain.AllLocations.Count + " MI-" + mazeIsland.AllLocations.Count + " Total-" + totalLocationsCount);
        bool updateItemsResult = false;
        while (prevCount != count || updateItemsResult)
        {
            prevCount = count;
            westHyrule.UpdateVisit(itemGet);
            deathMountain.UpdateVisit(itemGet);
            eastHyrule.UpdateVisit(itemGet);
            mazeIsland.UpdateVisit(itemGet);

            foreach (World world in worlds)
            {
                if (world.raft != null && CanGet(world.raft) && itemGet[Collectable.RAFT])
                {
                    worlds.ForEach(i => i.VisitRaft());
                }

                if (world.bridge != null && CanGet(world.bridge))
                {
                    worlds.ForEach(i => i.VisitBridge());
                }

                if (world.cave1 != null && CanGet(world.cave1))
                {
                    worlds.ForEach(i => i.VisitCave1());
                }

                if (world.cave2 != null && CanGet(world.cave2))
                {
                    worlds.ForEach(i => i.VisitCave2());
                }
            }
            updateItemsResult = UpdateItemGets();

            //This 2nd pass is weird and may not need to exist, eventually I should run some stats on whether it helps or not
            westHyrule.UpdateVisit(itemGet);
            deathMountain.UpdateVisit(itemGet);
            eastHyrule.UpdateVisit(itemGet);
            mazeIsland.UpdateVisit(itemGet);

            updateItemsResult |= UpdateItemGets();

            count = 0;
            dm = 0;
            mi = 0;
            wh = 0;
            eh = 0;

            wh = westHyrule.AllLocations.Count(i => i.Reachable);
            eh = eastHyrule.AllLocations.Count(i => i.Reachable);
            dm = deathMountain.AllLocations.Count(i => i.Reachable);
            mi = mazeIsland.AllLocations.Count(i => i.Reachable);
            count = wh + eh + dm + mi;

            //logger.Debug("Starting reachable main loop(" + loopCount++ + ". prevCount:" + prevCount + " count:" + count
            //+ " updateItemsResult:" + updateItemsResult + " updateSpellsResult:" + updateSpellsResult);
        }

        foreach(Collectable item in ItemGet.Keys)
        {
            if (ItemGet[item] == false && item.IsMajorItem())
            {
                itemGetReachableFailures++;
                if (UNSAFE_DEBUG && count >= DEBUG_THRESHOLD)
                {
                    Debug.WriteLine("Failed on items");
                    PrintRoutingDebug(count, wh, eh, dm, mi);
                    return false;
                }
                return false;
            }
        }
        if (accessibleMagicContainers != 8)
        {
            magicContainerReachableFailures++;
            //PrintRoutingDebug(count, wh, eh, dm, mi);
            return false;
        }
        if (heartContainers != maxHearts)
        {
            heartContainerReachableFailures++;
            //PrintRoutingDebug(count, wh, eh, dm, mi);
            return false;
        }
        /*
        if (ItemGet.Keys.Any(i => i.IsSpell() && !ItemGet[i]))
        {
            spellGetReachableFailures++;
            if (UNSAFE_DEBUG && count > DEBUG_THRESHOLD)
            {
                Debug.WriteLine("Failed on spells");
                debug++;
                PrintRoutingDebug(count, wh, eh, dm, mi);
                return false;
            }
            return false;
        }*/

        bool retval =
            CanGet(westHyrule.RequiredLocations(props.HiddenPalace, props.HiddenKasuto))
            && CanGet(eastHyrule.RequiredLocations(props.HiddenPalace, props.HiddenKasuto))
            && CanGet(deathMountain.RequiredLocations(props.HiddenPalace, props.HiddenKasuto))
            && CanGet(mazeIsland.RequiredLocations(props.HiddenPalace, props.HiddenKasuto));
        if (retval == false)
        {
            logicallyRequiredLocationsReachableFailures++;
            if(UNSAFE_DEBUG)
            {
                List<Location> missingLocations =
                [
                    .. westHyrule.RequiredLocations(props.HiddenPalace, props.HiddenKasuto).Where(i => !i.Reachable),
                    .. eastHyrule.RequiredLocations(props.HiddenPalace, props.HiddenKasuto).Where(i => !i.Reachable),
                    .. mazeIsland.RequiredLocations(props.HiddenPalace, props.HiddenKasuto).Where(i => !i.Reachable),
                ];

                List<Location> nonDmLocations = new(missingLocations);
                missingLocations.AddRange(deathMountain.RequiredLocations(props.HiddenPalace, props.HiddenKasuto).Where(i => !i.Reachable));

                Debug.WriteLine("Unreachable locations:");
                Debug.WriteLine(string.Join("\n", missingLocations.Select(i => i.GetDebuggerDisplay())));

                if(nonDmLocations.Count == 0 && UNSAFE_DEBUG)
                {
                    return false;
                }
                return false;
            }
        }
        if (UNSAFE_DEBUG && retval)
        {
            PrintRoutingDebug(count, wh, eh, dm, mi);
        }
        return retval;
    }

    private bool CanGet(IEnumerable<Location> locations)
    {
        return locations.All(i => i.Reachable);
    }
    private bool CanGet(Location location)
    {
        return location.Reachable;
    }

    /// <summary>
    /// Removes the intermediate room between entering the spell house and reaching the old man's basement
    /// </summary>
    private void ShortenWizards()
    {
        /*
        Spell swap notes:
        Shield exit: 0xC7BB, 0xC1; enter: 0xC7EC, 0x90 //change map 48 pointer to map 40 pointer
        Jump exit: 0xC7BF, 0xC5; enter: 0xC7F0, 0x94 //change map 49 pointer to map 41 pointer
        Life exit: 0xC7C3, 0xC9; enter 0xC7F4, 0x98 //change map 50 pointer to map 42 pointer
        Fairy exit: 0xC7C7, 0xCD; enter 0xC7F8, 0x9C //change map 51 pointer to map 43 pointer
        Fire exit: 0xC7Cb, 0xD1; enter 0xC7FC, 0xA0 //change map 52 pointer to map 44 pointer
        Reflect exit: 0xC7Cf, 0xD5; enter 0xC800, 0xA4 //change map 53 pointer to map 45 pointer
        Spell exit: 0xC7D3, 0x6A; enter 0xC795, 0xC796, 0x4D //new kasuto item?
        Thunder exit: 0xC7D7, 0xDD; enter 0xC808, 0xAC
        Downstab exit: 0xC7DB, 0xE1; enter 0xC80C, 0xB0
        Upstab exit: 0xC7DF, 0xE5; enter 0xC810, 0xB4
        */
        for (int i = 0; i < 16; i = i + 2)
        {
            ROMData.Put(0xC611 + i, (byte)0x75);
            ROMData.Put(0xC611 + i + 1, (byte)0x70);
            ROMData.Put(0xC593 + i, (byte)0x48);
            ROMData.Put(0xC593 + i + 1, (byte)0x9B);
        }
        ROMData.Put(0xC7BB, (byte)0x07);
        ROMData.Put(0xC7BF, (byte)0x13);
        ROMData.Put(0xC7C3, (byte)0x21);
        ROMData.Put(0xC7C7, (byte)0x27);
        ROMData.Put(0xC7CB, (byte)0x37);
        ROMData.Put(0xC7CF, (byte)0x3F);
        ROMData.Put(0xC850, (byte)0xB0);
        //ROMData.put(0xC7D3, (byte)0x4D);
        ROMData.Put(0xC7D7, (byte)0x5E);
        ROMData.Put(0xC7DF, (byte)0x43);
        ROMData.Put(0xC870, (byte)0xB8);
        ROMData.Put(0xC7E3, (byte)0x49);
        ROMData.Put(0xC874, (byte)0xA8);
        ROMData.Put(0xC7D3, (byte)0x4D);
        ROMData.Put(0xC7DB, (byte)0x29);
        //ROMData.put(0xC7E3, (byte)0x49);
        // ROMData.put(0xC874, (byte)0xA8);
        //ROMData.put(0x8560, (byte)0xBC);
    }

    /// <summary>
    /// 
    /// </summary>
    /// <returns>Whether any items were marked accessable</returns>
    private bool UpdateItemGets()
    {
        accessibleMagicContainers = 4 + AllLocationsForReal().Where(i => i.ItemGet == true && i.Collectable == Collectable.MAGIC_CONTAINER).Count();
        heartContainers = startHearts;
        bool changed = false;
        List<RequirementType> requireables = GetRequireables();
        Location newKasuto = eastHyrule.AllLocations.First(i => i.ActualTown == Town.NEW_KASUTO);
        foreach (Location location in AllLocationsForReal().Where(i => i.Collectable != Collectable.DO_NOT_USE))
        {
            bool hadItemPreviously = location.ItemGet;
            bool hasItemNow;
            //Location is a palace
            if (location.PalaceNumber > 0 && location.PalaceNumber < 7)
            {
                Palace palace = palaces[location.PalaceNumber - 1];
                hasItemNow = CanGet(location)
                    //All palaces inherently required fairy spell or magic key
                    && (ItemGet[Collectable.FAIRY_SPELL] || ItemGet[Collectable.MAGIC_KEY])
                    && palace.CanGetItem(requireables);
            }
            //Special handling for new kasuto's "linked" non-wizard collectables
            else if (location == eastHyrule.spellTower)
            {
                hasItemNow = CanGet(newKasuto) && ItemGet[Collectable.SPELL_SPELL];
            }
            else if (location == eastHyrule.newKasutoBasement)
            {
                hasItemNow = CanGet(newKasuto) && accessibleMagicContainers >= kasutoJars;
            }
            //Location is a town
            else if(location.ActualTown != 0)
            {
                hasItemNow = CanGet(location) && Towns.townSpellAndItemRequirements[location.ActualTown].AreSatisfiedBy(requireables);
            }
            else
            {
                //TODO: Remove all the needX flags and replace them with a set of requirements.
                hasItemNow = CanGet(location) && (!location.NeedHammer || ItemGet[Collectable.HAMMER]) && (!location.NeedFlute || ItemGet[Collectable.FLUTE]);
            }

            //Issue #3: Previously running UpdateItemGets multiple times could produce different results based on the sequence of times it ran
            //For items that were blocked by MC requirements, different orders of parsing the same world could check the MCs at different times
            //producing different results. Now it's not possible to "go back" in logic and call previously accessed items inaccesable.
            location.ItemGet = hasItemNow || hadItemPreviously;
            ItemGet[location.Collectable] = hasItemNow || hadItemPreviously;

            if (location.ItemGet && location.Collectable == Collectable.HEART_CONTAINER)
            {
                heartContainers++;
            }
            if (!hadItemPreviously && location.ItemGet)
            {
                changed = true;
            }
        }

        if (!props.PbagItemShuffle)
        {
            if (westHyrule.pbagCave.Reachable)
            {
                westHyrule.pbagCave.ItemGet = true;
            }
            if (eastHyrule.pbagCave1.Reachable)
            {
                eastHyrule.pbagCave1.ItemGet = true;
            }
            if (eastHyrule.pbagCave2.Reachable)
            {
                eastHyrule.pbagCave2.ItemGet = true;
            }
        }

        accessibleMagicContainers = 4 + AllLocationsForReal().Where(i => i.ItemGet == true && i.Collectable == Collectable.MAGIC_CONTAINER).Count();
        return changed;
    }
    private void RandomizeLifeOrMagicEffectiveness(ROM rom, bool isMag)
    {

        int numBanks = 7;
        int start = 0x1E2BF;
        if (isMag)
        {
            numBanks = 8;
            start = 0xD8B;
        }
        int[,] life = new int[numBanks, 8];
        for (int i = 0; i < numBanks; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                int lifeVal = rom.GetByte(start + (i * 8) + j);
                int highPart = (lifeVal & 0xF0) >> 4;
                int lowPart = lifeVal & 0x0F;
                life[i, j] = highPart * 8 + lowPart / 2;
            }
        }

        StatEffectiveness currentStatEffectiveness = isMag ? props.MagicEffectiveness : props.LifeEffectiveness;

        for (int j = 0; j < 8; j++)
        {
            for (int i = 0; i < numBanks; i++)
            {
                int nextVal = life[i, j];
                if (currentStatEffectiveness == StatEffectiveness.AVERAGE)
                {
                    int max = (int)(life[i, j] + life[i, j] * .5);
                    int min = (int)(life[i, j] - life[i, j] * .5);
                    if (!isMag)
                    {
                        min = (int)(life[i, j] - life[i, j] * .25);
                    }
                    if (j == 0)
                    {
                        nextVal = RNG.Next(min, Math.Min(max, 120));
                    }
                    else
                    {
                        nextVal = RNG.Next(min, Math.Min(max, 120));
                        if (nextVal > life[i, j - 1])
                        {
                            nextVal = life[i, j - 1];
                        }
                    }
                }
                else if (props.MagicEffectiveness == StatEffectiveness.LOW && isMag)
                {
                    nextVal = (int)(life[i, j] + (life[i, j] * .5));
                }
                else if (props.LifeEffectiveness == StatEffectiveness.HIGH && !isMag || props.MagicEffectiveness == StatEffectiveness.HIGH && isMag)
                {
                    nextVal = (int)(life[i, j] * .5);
                }

                if (isMag && nextVal > 120)
                {
                    nextVal = 120;
                }
                life[i, j] = nextVal;
            }
        }

        for (int i = 0; i < numBanks; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                int highPart = (life[i, j] / 8) << 4;
                int lowPart = (life[i, j] % 8);
                rom.Put(start + (i * 8) + j, (byte)(highPart + (lowPart * 2)));
            }
        }

    }

    private void RandomizeEnemyStats()
    {
        if (props.ShuffleEnemyHP)
        {
            RandomizeHP(0x5434, 0x5453);
            RandomizeHP(0x9434, 0x944E);
            RandomizeHP(0x11435, 0x11435);
            RandomizeHP(0x11437, 0x11454);
            RandomizeHP(0x13C86, 0x13C87);
            RandomizeHP(0x15434, 0x15438);
            RandomizeHP(0x15440, 0x15443);
            RandomizeHP(0x15445, 0x1544B);
            RandomizeHP(0x1544E, 0x1544E);
            RandomizeHP(0x12935, 0x12935);
            RandomizeHP(0x12937, 0x12954);
        }

        if (props.AttackEffectiveness == StatEffectiveness.MAX)
        {
            for (int i = 0; i < 8; i++)
            {
                ROMData.Put(0x1E67D + i, (byte)192);
            }
            ROMData.Put(0x005432, (byte)193);
            ROMData.Put(0x009432, (byte)193);
            ROMData.Put(0x11436, (byte)193);
            ROMData.Put(0x12936, (byte)193);
            ROMData.Put(0x15532, (byte)193);
            ROMData.Put(0x11437, (byte)192);
            ROMData.Put(0x1143F, (byte)192);
            ROMData.Put(0x12937, (byte)192);
            ROMData.Put(0x1293F, (byte)192);
            ROMData.Put(0x15445, (byte)192);
            ROMData.Put(0x15446, (byte)192);
            ROMData.Put(0x15448, (byte)192);
            ROMData.Put(0x15453, (byte)193);
            ROMData.Put(0x12951, (byte)227);

        }
    }

    private void RandomizeHP(int start, int end)
    {
        for (int i = start; i <= end; i++)
        {
            int newVal = 0;
            int val = (int)ROMData.GetByte(i);

            newVal = RNG.Next((int)(val * 0.5), (int)(val * 1.5));
            if (newVal > 255)
            {
                newVal = 255;
            }

            ROMData.Put(i, (byte)newVal);
        }
    }

    public List<RequirementType> GetRequireables()
    {
        List<RequirementType> requireables = new();

        foreach(Collectable item in ItemGet.Keys)
        {
            if (ItemGet[item] && item.AsRequirement() != null)
            {
                RequirementType? reqirement = item.AsRequirement();
                if(reqirement != null)
                {
                    requireables.Add((RequirementType)reqirement);
                }
            }
        }
        
        if (accessibleMagicContainers >= 5 || props.DisableMagicRecs)
        {
            requireables.Add(RequirementType.FIVE_CONTAINERS);
        }
        if (accessibleMagicContainers >= 6 || props.DisableMagicRecs)
        {
            requireables.Add(RequirementType.SIX_CONTAINERS);
        }
        if (accessibleMagicContainers >= 7 || props.DisableMagicRecs)
        {
            requireables.Add(RequirementType.SEVEN_CONTAINERS);
        }
        if (accessibleMagicContainers == 8 || props.DisableMagicRecs)
        {
            requireables.Add(RequirementType.EIGHT_CONTAINERS);
        }
        return requireables;
    }

    private void ProcessOverworld(Action<string> progress, CancellationToken ct)
    {
        if (props.ShuffleSmallItems)
        {
            RandomizeSmallItems(1, true);
            RandomizeSmallItems(1, false);
            RandomizeSmallItems(2, true);
            RandomizeSmallItems(2, false);
            RandomizeSmallItems(3, true);
            //shuffleSmallItems(4, true);
            //shuffleSmallItems(4, false);
        }
        do //while (!EverythingReachable()) ;
        {
            totalReachabilityOverworldAttempts++;

            //Generate a pathable set of continents
            do // } while (!AllContinentsHaveConnection(worlds));
            {
                totalContinentConnectionOverworldAttempts++;
                worlds = new List<World>();
                westHyrule = new WestHyrule(props, RNG, ROMData);
                deathMountain = new DeathMountain(props, RNG, ROMData);
                eastHyrule = new EastHyrule(props, RNG, ROMData);
                mazeIsland = new MazeIsland(props, RNG, ROMData);
                worlds.Add(westHyrule);
                worlds.Add(deathMountain);
                worlds.Add(eastHyrule);
                worlds.Add(mazeIsland);
                ResetTowns();

                if (props.ContinentConnections == ContinentConnectionType.NORMAL || props.ContinentConnections == ContinentConnectionType.RB_BORDER_SHUFFLE)
                {
                    westHyrule.LoadCave1(ROMData, 1);
                    westHyrule.LoadCave2(ROMData, 1);
                    westHyrule.LoadRaft(ROMData, 2);

                    deathMountain.LoadCave1(ROMData, 0);
                    deathMountain.LoadCave2(ROMData, 0);

                    eastHyrule.LoadRaft(ROMData, 0);
                    eastHyrule.LoadBridge(ROMData, 3);

                    mazeIsland.LoadBridge(ROMData, 2);
                }
                else if (props.ContinentConnections == ContinentConnectionType.TRANSPORTATION_SHUFFLE)
                {
                    List<int> chosen = new List<int>();
                    int type = RNG.Next(4);
                    if (props.WestBiome == Biome.VANILLA
                        || props.WestBiome == Biome.VANILLA_SHUFFLE
                        || props.DmBiome == Biome.VANILLA
                        || props.DmBiome == Biome.VANILLA_SHUFFLE)
                    {
                        type = 3;
                    }

                    SetTransportation(0, 1, type);
                    chosen.Add(type);
                    if (props.WestBiome == Biome.VANILLA
                        || props.WestBiome == Biome.VANILLA_SHUFFLE
                        || props.DmBiome == Biome.VANILLA
                        || props.DmBiome == Biome.VANILLA_SHUFFLE)
                    {
                        type = 0;
                    }
                    else
                    {
                        do
                        {
                            type = RNG.Next(4);
                        } while (chosen.Contains(type));
                    }
                    SetTransportation(0, 1, type);
                    chosen.Add(type);
                    if (props.WestBiome == Biome.VANILLA
                        || props.WestBiome == Biome.VANILLA_SHUFFLE
                        || props.EastBiome == Biome.VANILLA
                        || props.EastBiome == Biome.VANILLA_SHUFFLE)
                    {
                        type = 1;
                    }
                    else
                    {
                        do
                        {
                            type = RNG.Next(4);
                        } while (chosen.Contains(type));
                    }
                    SetTransportation(0, 2, type);
                    chosen.Add(type);
                    if (props.EastBiome == Biome.VANILLA
                        || props.EastBiome == Biome.VANILLA_SHUFFLE
                        || props.MazeBiome == Biome.VANILLA
                        || props.MazeBiome == Biome.VANILLA_SHUFFLE)
                    {
                        type = 2;
                    }
                    else
                    {
                        do
                        {
                            type = RNG.Next(4);
                        } while (chosen.Contains(type));
                    }
                    SetTransportation(2, 3, type);
                }
                else
                {
                    Location l1, l2;
                    List<int> doNotPick = new List<int>();
                    if (props.WestBiome == Biome.VANILLA || props.WestBiome == Biome.VANILLA_SHUFFLE)
                    {
                        doNotPick.Add(0);
                    }
                    if (props.EastBiome == Biome.VANILLA || props.EastBiome == Biome.VANILLA_SHUFFLE)
                    {
                        doNotPick.Add(2);
                    }
                    if (props.DmBiome == Biome.VANILLA || props.DmBiome == Biome.VANILLA_SHUFFLE)
                    {
                        doNotPick.Add(1);
                    }
                    if (props.MazeBiome == Biome.VANILLA || props.MazeBiome == Biome.VANILLA_SHUFFLE)
                    {
                        doNotPick.Add(3);
                    }

                    int raftw1 = RNG.Next(worlds.Count);

                    if (props.WestBiome == Biome.VANILLA || props.WestBiome == Biome.VANILLA_SHUFFLE)
                    {
                        raftw1 = 0;
                    }
                    else
                    {
                        while (doNotPick.Contains(raftw1))
                        {
                            raftw1 = RNG.Next(worlds.Count);
                        }
                    }

                    int raftw2 = RNG.Next(worlds.Count);
                    if (props.EastBiome == Biome.VANILLA || props.EastBiome == Biome.VANILLA_SHUFFLE)
                    {
                        raftw2 = 2;
                    }
                    else
                    {
                        do
                        {
                            raftw2 = RNG.Next(worlds.Count);
                        } while (raftw1 == raftw2 || doNotPick.Contains(raftw2));
                    }

                    l1 = worlds[raftw1].LoadRaft(ROMData, raftw2);
                    l2 = worlds[raftw2].LoadRaft(ROMData, raftw1);
                    connections[0] = (l1, l2);

                    int bridgew1 = RNG.Next(worlds.Count);
                    if (props.EastBiome == Biome.VANILLA || props.EastBiome == Biome.VANILLA_SHUFFLE)
                    {
                        bridgew1 = 2;
                    }
                    else
                    {
                        while (doNotPick.Contains(bridgew1))
                        {
                            bridgew1 = RNG.Next(worlds.Count);
                        }
                    }
                    int bridgew2 = RNG.Next(worlds.Count);
                    if (props.MazeBiome == Biome.VANILLA || props.MazeBiome == Biome.VANILLA_SHUFFLE)
                    {
                        bridgew2 = 3;
                    }
                    else
                    {
                        do
                        {
                            bridgew2 = RNG.Next(worlds.Count);
                        } while (bridgew1 == bridgew2 || doNotPick.Contains(bridgew2));
                    }

                    l1 = worlds[bridgew1].LoadBridge(ROMData, bridgew2);
                    l2 = worlds[bridgew2].LoadBridge(ROMData, bridgew1);
                    connections[1] = (l1, l2);

                    int c1w1 = RNG.Next(worlds.Count);
                    if (props.WestBiome == Biome.VANILLA || props.WestBiome == Biome.VANILLA_SHUFFLE)
                    {
                        c1w1 = 0;
                    }
                    else
                    {
                        while (doNotPick.Contains(c1w1))
                        {
                            c1w1 = RNG.Next(worlds.Count);
                        }
                    }
                    int c1w2 = RNG.Next(worlds.Count);
                    if (props.DmBiome == Biome.VANILLA || props.DmBiome == Biome.VANILLA_SHUFFLE)
                    {
                        c1w2 = 1;
                    }
                    else
                    {
                        do
                        {
                            c1w2 = RNG.Next(worlds.Count);
                        } while (c1w1 == c1w2 || doNotPick.Contains(c1w2));
                    }

                    l1 = worlds[c1w1].LoadCave1(ROMData, c1w2);
                    l2 = worlds[c1w2].LoadCave1(ROMData, c1w1);
                    connections[2] = (l1, l2);

                    int c2w1 = RNG.Next(worlds.Count);
                    if (props.WestBiome == Biome.VANILLA || props.WestBiome == Biome.VANILLA_SHUFFLE)
                    {
                        c2w1 = 0;
                    }
                    else
                    {
                        while (doNotPick.Contains(c2w1))
                        {
                            c2w1 = RNG.Next(worlds.Count);
                        }
                    }
                    int c2w2 = RNG.Next(worlds.Count);
                    if (props.DmBiome == Biome.VANILLA || props.DmBiome == Biome.VANILLA_SHUFFLE)
                    {
                        c2w2 = 1;
                    }
                    else
                    {
                        do
                        {
                            c2w2 = RNG.Next(worlds.Count);
                        } while (c2w1 == c2w2 || doNotPick.Contains(c2w2));
                    }

                    l1 = worlds[c2w1].LoadCave2(ROMData, c2w2);
                    l2 = worlds[c2w2].LoadCave2(ROMData, c2w1);
                    connections[3] = (l1, l2);
                }
            } while (!AllContinentsHaveConnection(worlds));

            int nonContinentGenerationAttempts = 0;
            int nonTerrainShuffleAttempt = 0;
            DateTime timestamp;
            //Shuffle everything else
            do //while (wtries < 10 && !EverythingReachable());
            {
                //GENERATE WEST
                bool shouldContinue = UpdateProgress(progress, ct, 2);
                if (!shouldContinue)
                {
                    return;
                }
                nonContinentGenerationAttempts++;
                timestamp = DateTime.Now;
                if (!westHyrule.AllReached)
                {
                    while (!westHyrule.Terraform(props, ROMData)) { totalWestGenerationAttempts++; }
                    totalWestGenerationAttempts++;
                }
                westHyrule.ResetVisitabilityState();
                timeSpentBuildingWest += (int)DateTime.Now.Subtract(timestamp).TotalMilliseconds;

                //GENERATE DM
                shouldContinue = UpdateProgress(progress, ct, 3);
                if (!shouldContinue)
                {
                    return;
                }
                timestamp = DateTime.Now;
                if (!deathMountain.AllReached)
                {
                    while (!deathMountain.Terraform(props, ROMData)) { totalDeathMountainGenerationAttempts++; }
                    totalDeathMountainGenerationAttempts++;
                }
                deathMountain.ResetVisitabilityState();
                timeSpentBuildingDM += (int)DateTime.Now.Subtract(timestamp).TotalMilliseconds;

                //GENERATE EAST
                shouldContinue = UpdateProgress(progress, ct, 4);
                if (!shouldContinue)
                {
                    return;
                }
                timestamp = DateTime.Now;
                if (!eastHyrule.AllReached)
                {
                    while (!eastHyrule.Terraform(props, ROMData)) { totalEastGenerationAttempts++; }
                    totalEastGenerationAttempts++;
                }
                eastHyrule.ResetVisitabilityState();
                timeSpentBuildingEast += (int)DateTime.Now.Subtract(timestamp).TotalMilliseconds;

                //GENERATE MAZE ISLAND
                shouldContinue = UpdateProgress(progress, ct, 5);
                if (!shouldContinue)
                {
                    return;
                }
                timestamp = DateTime.Now;
                if (!mazeIsland.AllReached)
                {
                    while (!mazeIsland.Terraform(props, ROMData)) { totalMazeIslandGenerationAttempts++; }
                    totalMazeIslandGenerationAttempts++;
                }
                mazeIsland.ResetVisitabilityState();
                timeSpentBuildingMI += (int)DateTime.Now.Subtract(timestamp).TotalMilliseconds;

                shouldContinue = UpdateProgress(progress, ct, 6);
                if (!shouldContinue)
                {
                    return;
                }


                //Then perform non-terrain shuffles looking for one that works.
                nonTerrainShuffleAttempt = 0;
                do
                {
                    foreach (Location location in westHyrule.AllLocations)
                    {
                        location.Reachable = false;
                    }

                    foreach (Location location in eastHyrule.AllLocations)
                    {
                        location.Reachable = false;
                    }

                    foreach (Location location in mazeIsland.AllLocations)
                    {
                        location.Reachable = false;
                    }
                    foreach (Location location in deathMountain.AllLocations)
                    {
                        location.Reachable = false;
                    }

                    eastHyrule.spellTower.Reachable = false;
                    westHyrule.ResetVisitabilityState();
                    eastHyrule.ResetVisitabilityState();
                    mazeIsland.ResetVisitabilityState();
                    deathMountain.ResetVisitabilityState();

                    //There was a spooky extra call to LoadItemLocs that used to be here that shouldn't be needed, but be aware.
                    westHyrule.SetStart();

                    ShufflePalaces();
                    LoadItemLocs();
                    ShuffleItems();


                    westHyrule.UpdateAllReached();
                    eastHyrule.UpdateAllReached();
                    mazeIsland.UpdateAllReached();
                    deathMountain.UpdateAllReached();

                    nonTerrainShuffleAttempt++;
                } while (nonTerrainShuffleAttempt < NON_TERRAIN_SHUFFLE_ATTEMPT_LIMIT && !IsEverythingReachable(ItemGet));

                if (nonTerrainShuffleAttempt != NON_TERRAIN_SHUFFLE_ATTEMPT_LIMIT)
                {
                    break;
                }
            } while (nonContinentGenerationAttempts < NON_CONTINENT_SHUFFLE_ATTEMPT_LIMIT);
        } while (!IsEverythingReachable(ItemGet));

        if (props.ShuffleOverworldEnemies)
        {
            foreach (World world in worlds)
            {
                world.ShuffleOverworldEnemies(ROMData, props.GeneratorsAlwaysMatch, props.MixLargeAndSmallEnemies);
            }
        }
    }

    private bool UpdateProgress(Action<string> progress, CancellationToken ct, int v)
    {
        if (ct.IsCancellationRequested)
        {
            return false;
        }
        switch (v)
        {
            case 2:
                progress.Invoke("Generating Western Hyrule");
                break;
            case 3:
                progress.Invoke("Generating Death Mountain");
                break;
            case 4:
                progress.Invoke("Generating East Hyrule");
                break;
            case 5:
                progress.Invoke("Generating Maze Island");
                break;
            case 6:
                progress.Invoke("Shuffling Items and Spells");
                break;
            case 7:
                progress.Invoke("Running Seed Completability Checks");
                break;
            case 8:
                progress.Invoke("Generating Hints");
                break;
            case 9:
                progress.Invoke("Finishing up");
                break;
        }
        return true;
    }

    private void SetTransportation(int w1, int w2, int type)
    {
        Location l1, l2;
        if (type == 1)
        {
            l1 = worlds[w1].LoadRaft(ROMData, w2);
            l2 = worlds[w2].LoadRaft(ROMData, w1);
            connections[0] = (l1, l2);
        }
        else if (type == 2)
        {
            l1 = worlds[w1].LoadBridge(ROMData, w2);
            l2 = worlds[w2].LoadBridge(ROMData, w1);
            connections[1] = (l1, l2);
        }
        else if (type == 3)
        {
            l1 = worlds[w1].LoadCave1(ROMData, w2);
            l2 = worlds[w2].LoadCave1(ROMData, w1);
            connections[2] = (l1, l2);
        }
        else
        {
            l1 = worlds[w1].LoadCave2(ROMData, w2);
            l2 = worlds[w2].LoadCave2(ROMData, w1);
            connections[3] = (l1, l2);
        }
    }

    private bool AllContinentsHaveConnection(List<World> worlds)
    {
        foreach (World w in worlds)
        {
            if (!w.HasConnections())
            {
                return false;
            }
        }
        return true;
    }

    private void ResetTowns()
    {
        westHyrule.locationAtRauru.ActualTown = Town.RAURU;
        westHyrule.locationAtRuto.ActualTown = Town.RUTO;
        westHyrule.locationAtSariaNorth.ActualTown = Town.SARIA_NORTH;
        westHyrule.locationAtSariaSouth.ActualTown = Town.SARIA_SOUTH;
        westHyrule.locationAtMido.ActualTown = Town.MIDO_WEST;
        eastHyrule.townAtNabooru.ActualTown = Town.NABOORU;
        eastHyrule.townAtDarunia.ActualTown = Town.DARUNIA_WEST;
        eastHyrule.townAtNewKasuto.ActualTown = Town.NEW_KASUTO;
        eastHyrule.townAtOldKasuto.ActualTown = Town.OLD_KASUTO;
    }

    private void ShufflePalaces()
    {

        if (props.SwapPalaceCont)
        {

            List<Location> pals = new List<Location> { westHyrule.locationAtPalace1, westHyrule.locationAtPalace2, westHyrule.locationAtPalace3, mazeIsland.locationAtPalace4, eastHyrule.locationAtPalace5, eastHyrule.locationAtPalace6 };

            if (props.P7shuffle)
            {
                pals.Add(eastHyrule.locationAtGP);
            }

            //Replaced with fisher-yates
            for (int i = pals.Count() - 1; i > 0; i--)
            {
                int swap = RNG.Next(i + 1);
                Util.Swap(pals[i], pals[swap]);
            }

            westHyrule.locationAtPalace1.World = westHyrule.locationAtPalace1.World & 0xFC;
            westHyrule.locationAtPalace2.World = westHyrule.locationAtPalace2.World & 0xFC;
            westHyrule.locationAtPalace3.World = westHyrule.locationAtPalace3.World & 0xFC;

            mazeIsland.locationAtPalace4.World = mazeIsland.locationAtPalace4.World & 0xFC;
            mazeIsland.locationAtPalace4.World = mazeIsland.locationAtPalace4.World | 0x03;

            eastHyrule.locationAtPalace5.World = eastHyrule.locationAtPalace5.World & 0xFC;
            eastHyrule.locationAtPalace5.World = eastHyrule.locationAtPalace5.World | 0x02;

            eastHyrule.locationAtPalace6.World = eastHyrule.locationAtPalace6.World & 0xFC;
            eastHyrule.locationAtPalace6.World = eastHyrule.locationAtPalace6.World | 0x02;

            if (props.P7shuffle)
            {
                eastHyrule.locationAtGP.World = eastHyrule.locationAtGP.World & 0xFC;
                eastHyrule.locationAtGP.World = eastHyrule.locationAtGP.World | 0x02;
            }
        }

    }

    //ItemLocs is specifically only those locations that contain shufflable items
    //If you're looking for a global reference for which items are where... too bad it doesn't exist :(
    private List<Location> LoadItemLocs()
    {
        itemLocs =
        [
            .. westHyrule.locationAtPalace1.PalaceNumber != 7 ? [westHyrule.locationAtPalace1] : (Location[])[],
            .. westHyrule.locationAtPalace2.PalaceNumber != 7 ? [westHyrule.locationAtPalace2] : (Location[])[],
            .. westHyrule.locationAtPalace3.PalaceNumber != 7 ? [westHyrule.locationAtPalace3] : (Location[])[],
            .. mazeIsland.locationAtPalace4.PalaceNumber != 7 ? [mazeIsland.locationAtPalace4] : (Location[])[],
            .. eastHyrule.locationAtPalace5.PalaceNumber != 7 ? [eastHyrule.locationAtPalace5] : (Location[])[],
            .. eastHyrule.locationAtPalace6.PalaceNumber != 7 ? [eastHyrule.locationAtPalace6] : (Location[])[],
            .. eastHyrule.locationAtGP.PalaceNumber != 7 ? [eastHyrule.locationAtGP] : (Location[])[],
            westHyrule.grassTile,
            westHyrule.heartContainerCave,
            westHyrule.magicContainerCave,
            westHyrule.medicineCave,
            westHyrule.trophyCave,
            eastHyrule.waterTile,
            eastHyrule.desertTile,
            eastHyrule.newKasutoBasement,
            eastHyrule.spellTower,
            deathMountain.specRock,
            deathMountain.hammerCave,
            mazeIsland.childDrop,
            mazeIsland.magicContainerDrop
        ];

        if(props.PbagItemShuffle)
        {
            itemLocs.Add(westHyrule.pbagCave);
            itemLocs.Add(eastHyrule.pbagCave1);
            itemLocs.Add(eastHyrule.pbagCave2);
        }

        if (props.IncludeQuestItemsInShuffle)
        {
            itemLocs.Add(westHyrule.bagu);
            itemLocs.Add(westHyrule.mirrorTable);
            itemLocs.Add(eastHyrule.fountain);
        }

        if (props.IncludeSpellsInShuffle)
        {
            itemLocs.Add(westHyrule.locationAtRauru);
            itemLocs.Add(westHyrule.locationAtRuto);
            itemLocs.Add(westHyrule.locationAtSariaNorth);
            itemLocs.Add(westHyrule.locationAtMido);
            itemLocs.Add(eastHyrule.townAtNabooru);
            itemLocs.Add(eastHyrule.townAtDarunia);
            itemLocs.Add(eastHyrule.townAtNewKasuto);
            itemLocs.Add(eastHyrule.townAtOldKasuto);
        }

        if (props.IncludeSwordTechsInShuffle)
        {
            itemLocs.Add(westHyrule.midoChurch);
            itemLocs.Add(eastHyrule.daruniaRoof);
        }

        return itemLocs;
    }

    /// <summary>
    /// Shuffles which spells are in which towns. 
    /// Specifically only used when shuffle spells is on but spells are not in the shuffle pool
    /// </summary>
    /// <exception cref="Exception"></exception>
    private void ShuffleSpells()
    {
        foreach(Collectable collectable in ItemGet.Keys)
        {
            if(collectable.IsSpell())// || collectable == Collectable.UPSTAB || collectable == Collectable.DOWNSTAB)
            {
                ItemGet[collectable] = false;
            }
        }

        List<Town> unallocatedTowns = new List<Town> { Town.RAURU, Town.RUTO, Town.SARIA_NORTH, Town.MIDO_WEST,
            Town.NABOORU, Town.DARUNIA_WEST, Town.NEW_KASUTO, Town.OLD_KASUTO };

        var filteredToJustSpells = Enum.GetValues(typeof(Collectable)).Cast<Collectable>().ToList()
            .Where(x => x.ToString().EndsWith("_SPELL"));
        foreach (Collectable spell in filteredToJustSpells)
        {
            if (!spell.IsSpell()
                || props.ReplaceFireWithDash && spell == Collectable.FIRE_SPELL
                || !props.ReplaceFireWithDash && spell == Collectable.DASH_SPELL
                || spell == Collectable.UPSTAB
                || spell == Collectable.DOWNSTAB)
            {
                continue;
            }
            Town town = unallocatedTowns[RNG.Next(unallocatedTowns.Count)];
            Location townLocation = GetTownLocation(town);
            townLocation.Collectable = spell;
            unallocatedTowns.Remove(town);

        }

        if (props.CombineFire)
        {
            int newFire = RNG.Next(7);
            if (newFire > 3)
            {
                newFire++;
            }
            byte newnewFire = (byte)(0x10 | ROMData.GetByte(0xDCB + newFire));
            ROMData.Put(0xDCF, newnewFire);
        }
    }

    private void HandleSwapUpAndDownstab()
    {
        eastHyrule.daruniaRoof.Collectable = Collectable.DOWNSTAB;
        westHyrule.midoChurch.Collectable = Collectable.UPSTAB;
    }

    private void LinkFire()
    {
        //LinkedFire
        int newFire = RNG.Next(7);
        if (newFire > 3)
        {
            newFire++;
        }
        byte newnewFire = (byte)(0x10 | ROMData.GetByte(0xDCB + newFire));
        ROMData.Put(0xDCF, newnewFire);
    }


    private void RandomizeExperience(ROM rom, int start, int cap)
    {
        int[] exp = new int[8];

        for (int i = 0; i < exp.Length; i++)
        {
            exp[i] = rom.GetByte(start + i) * 256;
            exp[i] = exp[i] + rom.GetByte(start + 24 + i);
        }

        for (int i = 0; i < exp.Length; i++)
        {
            int nextMin = (int)(exp[i] - exp[i] * 0.25);
            int nextMax = (int)(exp[i] + exp[i] * 0.25);
            if (i == 0)
            {
                exp[i] = RNG.Next(Math.Max(10, nextMin), nextMax);
            }
            else
            {
                exp[i] = RNG.Next(Math.Max(exp[i - 1], nextMin), Math.Min(nextMax, 9990));
            }
        }

        for (int i = 0; i < exp.Length; i++)
        {
            exp[i] = exp[i] / 10 * 10; //wtf is this line of code? -digshake, 2020
        }

        int[] cappedExp = new int[8];
        if (props.ScaleLevels)
        {
            for (int i = 0; i < exp.Length; i++)
            {
                if (i >= cap)
                {
                    cappedExp[i] = exp[i]; //shouldn't matter, just wanna put something here
                }
                else if (i == cap - 1)
                {
                    cappedExp[i] = exp[7]; //exp to get a 1up
                }
                else
                {
                    int index = (int)(8 * ((i + 1.0) / (cap - 1)));
                    cappedExp[i] = exp[(int)(6 * ((i + 1.0) / (cap - 1)))]; //cap = 3, level 4, 8, 
                }
            }
        }
        else
        {
            cappedExp = exp;
        }

        for (int i = 0; i < exp.Length; i++)
        {
            rom.Put(start + i, (byte)(cappedExp[i] / 256));
            rom.Put(start + 24 + i, (byte)(cappedExp[i] % 256));
        }

        for (int i = 0; i < exp.Length; i++)
        {

            rom.Put(start + 2057 + i, IntToText(cappedExp[i] / 1000));
            cappedExp[i] = cappedExp[i] - ((cappedExp[i] / 1000) * 1000);
            rom.Put(start + 2033 + i, IntToText(cappedExp[i] / 100));
            cappedExp[i] = cappedExp[i] - ((cappedExp[i] / 100) * 100);
            rom.Put(start + 2009 + i, IntToText(cappedExp[i] / 10));
        }
    }

    /// <summary>
    /// For a given set of addresses, set a masked portion of the value of each address on or off at a rate
    /// equal to the proportion of values at the addresses that have that masked portion set to a nonzero value.
    /// In effect, turn some values in a range on or off randomly in the proportion of the number of such values that are on in vanilla.
    /// </summary>
    /// <param name="addr">Addresses to randomize.</param>
    /// <param name="mask">What part of the byte value at each address contains the configuration we care about.</param>
    /// <exception cref="ArgumentException">Iff there are 0 addresses in the space to shuffle, as this would cause a divide by zero
    /// while determining the proportion.</exception>
    private void RandomizeBits(ROM rom, List<int> addr, int mask = 0b00010000)
    {
        if(addr.Count == 0)
        {
            throw new ArgumentException("Cannot shuffle 0 bits");
        }
        int notMask = mask ^ 0xFF;

        double count = 0;
        foreach (int i in addr)
        {
            if ((rom.GetByte(i) & mask) > 0)
            {
                count++;
            }
        }

        //proportion of the bytes that have nonzero values in the masked portion
        double fraction = count / addr.Count;

        foreach (int i in addr)
        {
            int part1 = 0;
            int part2 = rom.GetByte(i) & notMask;
            if (RNG.NextDouble() <= fraction)
            {
                part1 = mask;
            }
            rom.Put(i, (byte)(part1 + part2));
        }
    }

    private void RandomizeEnemyExp(ROM rom, List<int> addr)
    {
        foreach (int i in addr)
        {
            byte exp = rom.GetByte(i);
            int high = exp & 0xF0;
            int low = exp & 0x0F;

            if (props.ExpLevel == StatEffectiveness.HIGH)
            {
                low++;
            }
            else if (props.ExpLevel == StatEffectiveness.LOW)
            {
                low--;
            }
            else if (props.ExpLevel == StatEffectiveness.NONE)
            {
                low = 0;
            }

            if (props.ExpLevel != StatEffectiveness.NONE)
            {
                low = RNG.Next(low - 2, low + 3);
            }
            if (low < 0)
            {
                low = 0;
            }
            else if (low > 15)
            {
                low = 15;
            }
            rom.Put(i, (byte)(high + low));
        }
    }

    //Updated to use fisher-yates. Eventually i'll catch all of these. N is small enough here it REALLY makes a difference
    private void ShuffleEncounters(ROM rom, List<int> addr)
    {
        for (int i = addr.Count - 1; i > 0; --i)
        {
            int swap = RNG.Next(i + 1);

            byte temp = rom.GetByte(addr[i]);
            rom.Put(addr[i], rom.GetByte(addr[swap]));
            rom.Put(addr[swap], temp);
        }
    }
    private void RandomizeStartingValues(Assembler a, ROM rom)
    {

        rom.Put(0x17AF3, (byte)props.StartAtk);
        rom.Put(0x17AF4, (byte)props.StartMag);
        rom.Put(0x17AF5, (byte)props.StartLifeLvl);

        if (props.RemoveFlashing)
        {
            rom.DisableFlashing();
        }

        if (props.SpellEnemy)
        {
            //3, 4, 6, 7, 14, 16, 17, 18, 24, 25, 26
            List<int> enemies = new List<int> { 3, 4, 6, 7, 0x0E, 0x10, 0x11, 0x12, 0x18, 0x19, 0x1A };
            rom.Put(0x11ef, (byte)enemies[RNG.Next(enemies.Count)]);
        }
        if (props.BossItem)
        {
            shuffler.ShuffleBossDrop(rom, RNG, a);
        }

        if (props.StartWithSpellItems)
        {
            //ROMData.Put(0xF584, 0xA9);
            //ROMData.Put(0xF585, 0x01);
            //ROMData.Put(0xF586, 0xEA);
            //Instead of patching out the checks for the spell items, actually update the default save data so you start with them.
            rom.Put(0x17b14, 0x10); //Trophy
            rom.Put(0x17b15, 0x01); //Mirror
            rom.Put(0x17b16, 0x40); //Medicine
            rom.Put(0x17b17, 0x01); //Water
            rom.Put(0x17b18, 0x20); //Child
        }

        rom.UpdateSprites(props.CharSprite, props.TunicColor, props.ShieldColor, props.BeamSprite);
        rom.Put(0x20010 + 0x1a000, Assembly.GetExecutingAssembly().ReadBinaryResource("RandomizerCore.Asm.Graphics.item_sprites.chr"));

        if (props.EncounterRates == EncounterRate.NONE)
        {
            rom.Put(0x294, 0x60); //skips the whole routine
        }

        if (props.EncounterRates == EncounterRate.HALF)
        {
            //terrain timers
            rom.Put(0x250, 0x40);
            rom.Put(0x251, 0x30);
            rom.Put(0x252, 0x30);
            rom.Put(0x253, 0x40);
            rom.Put(0x254, 0x12);
            rom.Put(0x255, 0x06);

            //initial overworld timer
            rom.Put(0x88A, 0x10);

            /*
             * insert jump to a8aa at 2a3 (4c AA A8)
             * 
             * At a8aa
             * Load $26 (A5 26)
             * bne to end (2 bytes) (D0 0D)
             * inc new step counter (where?) EE E0 06
             * Load 1 to accumulator (A9 01)
             * xor new step counter with 1 (2D E0 06)
             * bne to end (D0 03)
             * jump to encounter spawn 8298 (4C 98 82)
             * jump to rts 829f (4C 93 82)
             */
            rom.Put(0x29f, new byte[] { 0x4C, 0xAA, 0xA8 });

            rom.Put(0x28ba, new byte[] { 0xA5, 0x26, 0xD0, 0x0D, 0xEE, 0xE0, 0x06, 0xA9, 0x01, 0x2D, 0xE0, 0x06, 0xD0, 0x03, 0x4C, 0x98, 0x82, 0x4C, 0x93, 0x82 });
        }

        //CMP      #$20                      ; 0x1d4e4 $D4D4 C9 20
        rom.Put(0x1d4e5, props.BeepThreshold);
        if (props.BeepFrequency == 0)
        {
            //C9 20 - EA 38
            //CMP 20 -> NOP SEC
            rom.Put(0x1D4E4, (byte)0xEA);
            rom.Put(0x1D4E5, (byte)0x38);
        }
        else
        {
            //LDA      #$30                      ; 0x193c1 $93B1 A9 30
            rom.Put(0x193c2, props.BeepFrequency);
        }

        if (props.ShuffleLifeRefill)
        {
            int lifeRefill = RNG.Next(1, 6);
            rom.Put(0xE7A, (byte)(lifeRefill * 16));
        }

        if (props.ShuffleStealExpAmt)
        {
            int small = rom.GetByte(0x1E30E);
            int big = rom.GetByte(0x1E314);
            small = RNG.Next((int)(small - small * .5), (int)(small + small * .5) + 1);
            big = RNG.Next((int)(big - big * .5), (int)(big + big * .5) + 1);
            rom.Put(0x1E30E, (byte)small);
            rom.Put(0x1E314, (byte)big);
        }

        List<int> addr = new List<int>();
        for (int i = 0x54E8; i < 0x54ED; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x54EF; i < 0x54F8; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x54F9; i < 0x5508; i++)
        {
            addr.Add(i);
        }

        if (props.ShuffleEnemyStealExp)
        {
            RandomizeBits(rom, addr);
        }

        if (props.ShuffleSwordImmunity)
        {
            RandomizeBits(rom, addr, 0x20);
        }

        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            RandomizeEnemyExp(rom, addr);
        }
        addr = new List<int>();
        for (int i = 0x94E8; i < 0x94ED; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x94EF; i < 0x94F8; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x94F9; i < 0x9502; i++)
        {
            addr.Add(i);
        }
        if (props.ShuffleEnemyStealExp)
        {
            RandomizeBits(rom, addr);
        }

        if (props.ShuffleSwordImmunity)
        {
            RandomizeBits(rom, addr, 0x20);
        }
        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            RandomizeEnemyExp(rom, addr);
        }

        addr = new List<int>();
        for (int i = 0x114E8; i < 0x114EA; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x114EB; i < 0x114ED; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x114EF; i < 0x114F8; i++)
        {
            addr.Add(i);
        }
        for (int i = 0x114FD; i < 0x11505; i++)
        {
            addr.Add(i);
        }
        addr.Add(0x11508);

        if (props.ShuffleEnemyStealExp)
        {
            RandomizeBits(rom, addr);
        }

        if (props.ShuffleSwordImmunity)
        {
            RandomizeBits(rom, addr, 0x20);
        }
        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            RandomizeEnemyExp(rom, addr);
        }

        addr = new List<int>();
        for (int i = 0x129E8; i < 0x129EA; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x129EB; i < 0x129ED; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x129EF; i < 0x129F4; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x129F5; i < 0x129F7; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x129FD; i < 0x12A05; i++)
        {
            addr.Add(i);
        }

        addr.Add(0x12A08);

        if (props.ShuffleEnemyStealExp)
        {
            RandomizeBits(rom, addr);
        }

        if (props.ShuffleSwordImmunity)
        {
            RandomizeBits(rom, addr, 0x20);
        }
        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            RandomizeEnemyExp(rom, addr);
        }

        addr = new List<int>();
        for (int i = 0x154E9; i < 0x154ED; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x154F2; i < 0x154F8; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x154F9; i < 0x15500; i++)
        {
            addr.Add(i);
        }

        for (int i = 0x15502; i < 15504; i++)
        {
            addr.Add(i);
        }

        if (props.ShuffleEnemyStealExp)
        {
            RandomizeBits(rom, addr);
        }

        if (props.ShuffleSwordImmunity)
        {
            RandomizeBits(rom, addr, 0x20);
        }
        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            RandomizeEnemyExp(rom, addr);
        }

        if (props.ExpLevel != StatEffectiveness.VANILLA)
        {
            addr = new List<int>();
            addr.Add(0x11505);
            addr.Add(0x13C88);
            addr.Add(0x13C89);
            addr.Add(0x12A05);
            addr.Add(0x12A06);
            addr.Add(0x12A07);
            addr.Add(0x15507);
            RandomizeEnemyExp(rom, addr);
        }

        if (props.ShuffleEncounters)
        {
            addr = new List<int>();
            addr.Add(0x441b); // 0x62: West northern grass
            addr.Add(0x4419); // 0x5D: West northern desert
            addr.Add(0x441D); // 0x67: West northern forest
            addr.Add(0x4420); // 0x70: West swamp
            addr.Add(0x441C); // 0x63: West south grass
            addr.Add(0x441A); // 0x5E: West south desert
            addr.Add(0x4422); // 0x75: West grave
            addr.Add(0x441E); // 0x68: West south forest

            if (props.AllowPathEnemies)
            {
                addr.Add(0x4424);
                addr.Add(0x4423);
            }

            ShuffleEncounters(rom, addr);

            addr = new List<int>();
            addr.Add(0x841B); // 0x62: East grass
            addr.Add(0x8419); // 0x5D: East desert
            addr.Add(0x841D); // 0x67: East forest
            addr.Add(0x8422); // 0x75: East grave
            addr.Add(0x8420); // 0x70: East swamp
            addr.Add(0x841A); // 0x5E: East south desert
            addr.Add(0x841E); // 0x68: East south forest
            if (props.IncludeLavaInEncounterShuffle)
            {
                addr.Add(0x8426); // 0x7C: Valley of death
            }

            if (props.AllowPathEnemies)
            {
                addr.Add(0x8423);
                addr.Add(0x8424);
            }

            ShuffleEncounters(rom, addr);
        }

        if (props.JumpAlwaysOn)
        {
            rom.Put(0x1482, rom.GetByte(0x1480));
            rom.Put(0x1483, rom.GetByte(0x1481));
            rom.Put(0x1486, rom.GetByte(0x1484));
            rom.Put(0x1487, rom.GetByte(0x1485));

        }

        if (props.DisableMagicRecs)
        {
            rom.Put(0xF539, (byte)0xC9);
            rom.Put(0xF53A, (byte)0);
        }

        if (props.ShuffleAtkExp)
        {
            RandomizeExperience(rom, 0x1669, props.AttackCap);
        }

        if (props.ShuffleMagicExp)
        {
            RandomizeExperience(rom, 0x1671, props.MagicCap);
        }

        if (props.ShuffleLifeExp)
        {
            RandomizeExperience(rom, 0x1679, props.LifeCap);
        }

        rom.SetLevelCap(props.AttackCap, props.MagicCap, props.LifeCap);

        RandomizeAttackEffectiveness(rom, props.AttackEffectiveness);

        RandomizeLifeOrMagicEffectiveness(rom, true);

        RandomizeLifeOrMagicEffectiveness(rom, false);

        rom.Put(0x17B10, (byte)props.StartGems);


        startHearts = props.StartHearts;
        rom.Put(0x17B00, (byte)startHearts);


        maxHearts = props.MaxHearts;

        heartContainersInItemPool = maxHearts - startHearts;


        rom.Put(0x1C369, (byte)props.StartLives);

        rom.Put(0x17B12, (byte)((props.StartWithUpstab ? 0x04 : 0) + (props.StartWithDownstab ? 0x10 : 0)));

        //Swap up and Downstab
        if (props.SwapUpAndDownStab)
        {
            //Swap the ORAs that determine which stab to give you
            rom.Put(0xF4DF, 0x04);
            rom.Put(0xF4F7, 0x10);
            //Swap the ANDs that check whether or not you have the stab
            rom.Put(0xF4D3, 0x04);
            rom.Put(0xF4EB, 0x10);
        }

        if (props.LifeEffectiveness == StatEffectiveness.MAX)
        {
            for (int i = 0x1E2BF; i < 0x1E2BF + 56; i++)
            {
                rom.Put(i, 0);
            }
        }

        if (props.LifeEffectiveness == StatEffectiveness.NONE)
        {
            for (int i = 0x1E2BF; i < 0x1E2BF + 56; i++)
            {
                rom.Put(i, 0xFF);
            }
        }

        if (props.MagicEffectiveness == StatEffectiveness.MAX)
        {
            for (int i = 0xD8B; i < 0xD8b + 64; i++)
            {
                rom.Put(i, 0);
            }
        }

        if (props.ShufflePalacePalettes)
        {
            shuffler.ShufflePalacePalettes(rom, RNG);
        }

        if (props.ShuffleItemDropFrequency)
        {
            int drop = RNG.Next(5) + 4;
            rom.Put(0x1E8B0, (byte)drop);
        }

    }

    private byte IntToText(int x)
    {
        switch (x)
        {
            case 0:
                return (byte)0xD0;
            case 1:
                return (byte)0xD1;
            case 2:
                return (byte)0xD2;
            case 3:
                return (byte)0xD3;
            case 4:
                return (byte)0xD4;
            case 5:
                return (byte)0xD5;
            case 6:
                return (byte)0xD6;
            case 7:
                return (byte)0xD7;
            case 8:
                return (byte)0xD8;
            default:
                return (byte)0xD9;
        }
    }

    private void UpdateRom(byte[] hash)
    {
        foreach (World world in worlds)
        {
            List<Location> locs = world.AllLocations;
            foreach (Location location in locs)
            {
                location.UpdateBytes();
                ROMData.Put(location.MemAddress, location.LocationBytes[0]);
                ROMData.Put(location.MemAddress + overworldXOff, location.LocationBytes[1]);
                ROMData.Put(location.MemAddress + overworldMapOff, location.LocationBytes[2]);
                ROMData.Put(location.MemAddress + overworldWorldOff, location.LocationBytes[3]);
            }
            ROMData.RemoveUnusedConnectors(world);
        }

        // Copy heart and medicine container sprite tiles to the new location.
        for (int i = 0; i < 64; i++)
        {
            byte heartByte = ROMData.GetByte(0x27810 + i);
            ROMData.Put(0x29810 + i, heartByte);
            ROMData.Put(0x2B810 + i, heartByte);
            ROMData.Put(0x2D810 + i, heartByte);
            ROMData.Put(0x33810 + i, heartByte);
            ROMData.Put(0x35810 + i, heartByte);
            ROMData.Put(0x37810 + i, heartByte);
            ROMData.Put(0x39810 + i, heartByte);
        }


        /*
        Location medicineLoc = null;
        Location trophyLoc = null;
        Location kidLoc = null;
        foreach (Location location in itemLocs)
        {
            if (location.Collectable == Collectable.MEDICINE)
            {
                medicineLoc = location;
            }
            if (location.Collectable == Collectable.TROPHY)
            {
                trophyLoc = location;
            }
            if (location.Collectable == Collectable.CHILD)
            {
                kidLoc = location;
            }
        }

        byte[] medSprite = new byte[32];
        byte[] trophySprite = new byte[32];
        byte[] kidSprite = new byte[32];

        for (int i = 0; i < 32; i++)
        {
            medSprite[i] = ROMData.GetByte(0x23310 + i);
            trophySprite[i] = ROMData.GetByte(0x232f0 + i);
            kidSprite[i] = ROMData.GetByte(0x25310 + i);
        }
        bool medEast = (eastHyrule.AllLocations.Contains(medicineLoc) || mazeIsland.AllLocations.Contains(medicineLoc));
        bool trophyEast = (eastHyrule.AllLocations.Contains(trophyLoc) || mazeIsland.AllLocations.Contains(trophyLoc));
        bool kidWest = (westHyrule.AllLocations.Contains(kidLoc) || deathMountain.AllLocations.Contains(kidLoc));
        Dictionary<int, int> palaceMems = new Dictionary<int, int>();
        palaceMems.Add(1, 0x29AD0);
        palaceMems.Add(2, 0x2BAD0);
        palaceMems.Add(3, 0x33AD0);
        palaceMems.Add(4, 0x35AD0);
        palaceMems.Add(5, 0x37AD0);
        palaceMems.Add(6, 0x39AD0);

        if (medEast && eastHyrule.locationAtPalace5.Collectable != Item.MEDICINE && eastHyrule.locationAtPalace6.Collectable != Item.MEDICINE && mazeIsland.locationAtPalace4.Collectable != Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x25430 + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0x43);
            ROMData.Put(0x1eeba, 0x43);
        }

        if (trophyEast)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x25410 + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0x41);
            ROMData.Put(0x1eeb8, 0x41);
        }

        if (kidWest && westHyrule.locationAtPalace1.Collectable != Item.CHILD && westHyrule.locationAtPalace2.Collectable != Item.CHILD && westHyrule.locationAtPalace3.Collectable != Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x23570 + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0x57);
            ROMData.Put(0x1eeb6, 0x57);
        }

        if (eastHyrule.townAtNewKasuto.Collectable == Item.TROPHY || eastHyrule.spellTower.Collectable == Item.TROPHY || westHyrule.locationAtSariaNorth.Collectable == Item.TROPHY || westHyrule.locationAtSariaSouth.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x27210 + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0x21);
            ROMData.Put(0x1eeb8, 0x21);
        }

        if (eastHyrule.townAtNewKasuto.Collectable == Item.MEDICINE || eastHyrule.spellTower.Collectable == Item.MEDICINE || westHyrule.locationAtSariaNorth.Collectable == Item.TROPHY || westHyrule.locationAtSariaSouth.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x27230 + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0x23);
            ROMData.Put(0x1eeba, 0x23);
        }

        if (eastHyrule.townAtNewKasuto.Collectable == Item.CHILD || eastHyrule.spellTower.Collectable == Item.CHILD || westHyrule.locationAtSariaNorth.Collectable == Item.TROPHY || westHyrule.locationAtSariaSouth.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(0x27250 + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0x25);
            ROMData.Put(0x1eeb6, 0x25);
        }

        if (westHyrule.locationAtPalace1.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace1.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (westHyrule.locationAtPalace2.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace2.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (westHyrule.locationAtPalace3.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace3.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (mazeIsland.locationAtPalace4.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[mazeIsland.locationAtPalace4.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (eastHyrule.locationAtPalace5.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace5.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (eastHyrule.locationAtPalace6.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace6.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }
        if (eastHyrule.locationAtGP.Collectable == Item.TROPHY)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtGP.PalaceNumber] + i, trophySprite[i]);
            }
            ROMData.Put(0x1eeb7, 0xAD);
            ROMData.Put(0x1eeb8, 0xAD);
        }

        if (westHyrule.locationAtPalace1.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace1.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (westHyrule.locationAtPalace2.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace2.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (westHyrule.locationAtPalace3.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace3.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (mazeIsland.locationAtPalace4.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[mazeIsland.locationAtPalace4.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (eastHyrule.locationAtPalace5.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace5.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (eastHyrule.locationAtPalace6.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace6.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }
        if (eastHyrule.locationAtGP.Collectable == Item.MEDICINE)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtGP.PalaceNumber] + i, medSprite[i]);
            }
            ROMData.Put(0x1eeb9, 0xAD);
            ROMData.Put(0x1eeba, 0xAD);
        }

        if (westHyrule.locationAtPalace1.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace1.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (westHyrule.locationAtPalace2.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace2.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (westHyrule.locationAtPalace3.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[westHyrule.locationAtPalace3.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (mazeIsland.locationAtPalace4.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[mazeIsland.locationAtPalace4.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (eastHyrule.locationAtPalace5.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace5.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (eastHyrule.locationAtPalace6.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtPalace6.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        if (eastHyrule.locationAtGP.Collectable == Item.CHILD)
        {
            for (int i = 0; i < 32; i++)
            {
                ROMData.Put(palaceMems[eastHyrule.locationAtGP.PalaceNumber] + i, kidSprite[i]);
            }
            ROMData.Put(0x1eeb5, 0xAD);
            ROMData.Put(0x1eeb6, 0xAD);
        }
        */

        foreach (Palace palace in palaces)
        {
            palace.ValidateRoomConnections();
            palace.UpdateRom(ROMData);
        }

        ROMData.AddCredits();
        //fixes improper exit from p6/new kasuto
        //if (eastHyrule.palace6.PalNum != 7)
        //{
        //    ROMData.put(0x1ccd3, 0xea);
        //    ROMData.put(0x1ccd4, 0xea);
        //    ROMData.put(0x1ccd5, 0xea);
        //    ROMData.put(0x1ccd6, 0xc0);
        //    ROMData.put(0x1ccd7, 0x31);
        //}


        ROMData.Put(0x1CD3A, (byte)palGraphics[westHyrule.locationAtPalace1.PalaceNumber]);


        ROMData.Put(0x1CD3B, (byte)palGraphics[westHyrule.locationAtPalace2.PalaceNumber]);


        ROMData.Put(0x1CD3C, (byte)palGraphics[westHyrule.locationAtPalace3.PalaceNumber]);


        ROMData.Put(0x1CD46, (byte)palGraphics[mazeIsland.locationAtPalace4.PalaceNumber]);


        ROMData.Put(0x1CD42, (byte)palGraphics[eastHyrule.locationAtPalace5.PalaceNumber]);

        ROMData.Put(0x1CD43, (byte)palGraphics[eastHyrule.locationAtPalace6.PalaceNumber]);
        ROMData.Put(0x1CD44, (byte)palGraphics[eastHyrule.locationAtGP.PalaceNumber]);

        if (props.ShuffleDripper)
        {
            ROMData.Put(0x11927, (byte)Enemies.Palace125Enemies[RNG.Next(Enemies.Palace125Enemies.Length)]);
        }

        if (props.ShuffleEnemyPalettes)
        {
            List<int> doubleLocs = [0x40b4, 0x80b4, 0x100b4, 0x100b8, 0x100bc, 0x140b4, 0x140b8, 0x140bc];
            List<int> singleLocs = [0x40b8, 0x40bc, 0x80b8, 0x80bc];

            foreach (int i in doubleLocs)
            {
                int low = RNG.Next(12) + 1;
                int high = (RNG.Next(2) + 1) * 16;
                int color = high + low;
                ROMData.Put(i, (byte)color);
                ROMData.Put(i + 16, (byte)color);
                ROMData.Put(i - 1, (byte)(color - 15));
                ROMData.Put(i + 16 - 1, (byte)(color - 15));
            }
            foreach (int i in singleLocs)
            {
                int low = RNG.Next(13);
                int high = (RNG.Next(3)) * 16;
                int color = high + low;
                ROMData.Put(i, (byte)color);
                ROMData.Put(i + 16, (byte)color);
                ROMData.Put(i + 16 - 1, (byte)(color - 15));
            }

            for (int i = 0x54e8; i < 0x5508; i++)
            {
                if (i != 0x54f8)
                {
                    int b = ROMData.GetByte(i);
                    int p = b & 0x3F;
                    int n = RNG.Next(4);
                    n = n << 6;
                    ROMData.Put(i, (byte)(n + p));
                }
            }

            for (int i = 0x94e8; i < 0x9508; i++)
            {
                if (i != 0x94f8)
                {
                    int b = ROMData.GetByte(i);
                    int p = b & 0x3F;
                    int n = RNG.Next(4);
                    n = n << 6;
                    ROMData.Put(i, (byte)(n + p));
                }
            }
            for (int i = 0x114e8; i < 0x11508; i++)
            {
                if (i != 0x114f8)
                {
                    int b = ROMData.GetByte(i);
                    int p = b & 0x3F;
                    int n = RNG.Next(4);
                    n = n << 6;
                    ROMData.Put(i, (byte)(n + p));
                }
            }
            for (int i = 0x129e8; i < 0x12a09; i++)
            {
                if (i != 0x129f8)
                {
                    int b = ROMData.GetByte(i);
                    int p = b & 0x3F;
                    int n = RNG.Next(4);
                    n = n << 6;
                    ROMData.Put(i, (byte)(n + p));
                }
            }
            for (int i = 0x154e8; i < 0x15508; i++)
            {
                if (i != 0x154f8)
                {
                    int b = ROMData.GetByte(i);
                    int p = b & 0x3F;
                    int n = RNG.Next(4);
                    n = n << 6;
                    ROMData.Put(i, (byte)(n + p));
                }
            }
        }

        //WRITE UPDATES TO WIZARD/QUEST COLLECTABLES HERE

        ROMData.Put(0x4DEA, (byte)westHyrule.trophyCave.Collectable);
        ROMData.Put(0x502A, (byte)westHyrule.magicContainerCave.Collectable);
        ROMData.Put(0x4DD7, (byte)westHyrule.heartContainerCave.Collectable);

        int[] itemLocs2 = { 0x10E91, 0x10E9A, 0x1252D, 0x12538, 0x10EA3, 0x12774 };

        ROMData.Put(0x5069, (byte)westHyrule.medicineCave.Collectable);
        ROMData.Put(0x4ff5, (byte)westHyrule.grassTile.Collectable);

        ROMData.Put(0x65C3, (byte)deathMountain.specRock.Collectable);
        ROMData.Put(0x6512, (byte)deathMountain.hammerCave.Collectable);
        ROMData.Put(0x8FAA, (byte)eastHyrule.waterTile.Collectable);
        ROMData.Put(0x9011, (byte)eastHyrule.desertTile.Collectable);
        //if (props.NormalPalaceStyle == PalaceStyle.RECONSTRUCTED)
        //{
        ROMData.ElevatorBossFix(props.BossItem);
        if (westHyrule.locationAtPalace1.PalaceNumber != 7)
        {
            palaces[westHyrule.locationAtPalace1.PalaceNumber - 1].UpdateItem(westHyrule.locationAtPalace1.Collectable, ROMData);
        }
        if (westHyrule.locationAtPalace2.PalaceNumber != 7)
        {
            palaces[westHyrule.locationAtPalace2.PalaceNumber - 1].UpdateItem(westHyrule.locationAtPalace2.Collectable, ROMData);
        }
        if (westHyrule.locationAtPalace3.PalaceNumber != 7)
        {
            palaces[westHyrule.locationAtPalace3.PalaceNumber - 1].UpdateItem(westHyrule.locationAtPalace3.Collectable, ROMData);
        }
        if (mazeIsland.locationAtPalace4.PalaceNumber != 7)
        {
            palaces[mazeIsland.locationAtPalace4.PalaceNumber - 1].UpdateItem(mazeIsland.locationAtPalace4.Collectable, ROMData);
        }
        if (eastHyrule.locationAtPalace5.PalaceNumber != 7)
        {
            palaces[eastHyrule.locationAtPalace5.PalaceNumber - 1].UpdateItem(eastHyrule.locationAtPalace5.Collectable, ROMData);
        }
        if (eastHyrule.locationAtPalace6.PalaceNumber != 7)
        {
            palaces[eastHyrule.locationAtPalace6.PalaceNumber - 1].UpdateItem(eastHyrule.locationAtPalace6.Collectable, ROMData);
        }
        if (eastHyrule.locationAtGP.PalaceNumber != 7)
        {
            palaces[eastHyrule.locationAtGP.PalaceNumber - 1].UpdateItem(eastHyrule.locationAtGP.Collectable, ROMData);
        }

        Room root = palaces[westHyrule.locationAtPalace1.PalaceNumber - 1].Root;
        ROMData.Put(westHyrule.locationAtPalace1.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[westHyrule.locationAtPalace2.PalaceNumber - 1].Root;
        ROMData.Put(westHyrule.locationAtPalace2.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[westHyrule.locationAtPalace3.PalaceNumber - 1].Root;
        ROMData.Put(westHyrule.locationAtPalace3.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[eastHyrule.locationAtPalace5.PalaceNumber - 1].Root;
        ROMData.Put(eastHyrule.locationAtPalace5.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[eastHyrule.locationAtPalace6.PalaceNumber - 1].Root;
        ROMData.Put(eastHyrule.locationAtPalace6.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[eastHyrule.locationAtGP.PalaceNumber - 1].Root;
        ROMData.Put(eastHyrule.locationAtGP.MemAddress + 0x7e, (byte)root.Map);
        root = palaces[mazeIsland.locationAtPalace4.PalaceNumber - 1].Root;
        ROMData.Put(mazeIsland.locationAtPalace4.MemAddress + 0x7e, (byte)root.Map);

        ROMData.Put(0xDB95, (byte)eastHyrule.spellTower.Collectable); //map 47
        ROMData.Put(0xDB8C, (byte)eastHyrule.townAtNewKasuto.Collectable); //map 46

        ROMData.Put(0xA5A8, (byte)mazeIsland.magicContainerDrop.Collectable);
        ROMData.Put(0xA58B, (byte)mazeIsland.childDrop.Collectable);

        if (props.PbagItemShuffle)
        {
            ROMData.Put(0x4FE2, (byte)westHyrule.pbagCave.Collectable);
            ROMData.Put(0x8ECC, (byte)eastHyrule.pbagCave1.Collectable);
            ROMData.Put(0x8FB3, (byte)eastHyrule.pbagCave2.Collectable);

        }

        foreach (Location location in pbagHearts)
        {
            if (location == westHyrule.pbagCave)
            {
                ROMData.Put(0x4FE2, (byte)westHyrule.pbagCave.Collectable);
            }

            if (location == eastHyrule.pbagCave1)
            {
                ROMData.Put(0x8ECC, (byte)eastHyrule.pbagCave1.Collectable);
            }
            if (location == eastHyrule.pbagCave2)
            {
                ROMData.Put(0x8FB3, (byte)eastHyrule.pbagCave2.Collectable);
            }
        }

        //Update raft animation
        bool firstRaft = false;
        foreach (World w in worlds)
        {
            if (w.raft != null)
            {
                if (!firstRaft)
                {
                    ROMData.Put(0x538, (byte)w.raft.Xpos);
                    ROMData.Put(0x53A, (byte)w.raft.Ypos);
                    firstRaft = true;
                }
                else
                {
                    ROMData.Put(0x539, (byte)w.raft.Xpos);
                    ROMData.Put(0x53B, (byte)w.raft.Ypos);
                }
            }
        }

        firstRaft = false;
        //Fix Maze Island Bridge music bug
        foreach (World w in worlds)
        {
            if (w.bridge != null)
            {
                if (!firstRaft)
                {
                    ROMData.Put(0x565, (byte)w.bridge.Xpos);
                    ROMData.Put(0x567, (byte)w.bridge.Ypos);
                    firstRaft = true;
                }
                else
                {
                    ROMData.Put(0x564, (byte)w.bridge.Xpos);
                    ROMData.Put(0x566, (byte)w.bridge.Ypos);
                }
            }
        }

        //Update world check for p7
        if (westHyrule.locationAtPalace1.PalaceNumber == 7 || westHyrule.locationAtPalace2.PalaceNumber == 7 || westHyrule.locationAtPalace3.PalaceNumber == 7)
        {
            ROMData.Put(0x1dd3b, 0x05);
        }

        if (mazeIsland.locationAtPalace4.PalaceNumber == 7)
        {
            ROMData.Put(0x1dd3b, 0x14);
        }

        int spellNameBase = 0x1c3a, effectBase = 0x00e58, spellCostBase = 0xd8b, functionBase = 0xdcb;

        int[,] magLevels = new int[8, 8];
        int[,] magNames = new int[8, 7];
        int[] magEffects = new int[16];
        int[] magFunction = new int[8];
        //ROMData.UpdateWizardText(WizardCollectables);

        //XXX: We discussed this in discord and the majority wanted the spell list in vanilla order,
        //but also this should probably work the way it used to when full spell shuffle is off
        //regardless it needs to be refactored because WizardCollectables is dead
        /*
        for (int i = 0; i < magFunction.Count(); i++)
        {
            magFunction[i] = ROMData.GetByte(functionBase + WizardCollectables[Towns.STRICT_SPELL_LOCATIONS[i]].VanillaSpellOrder());
        }

        for (int i = 0; i < magEffects.Count(); i = i + 2)
        {
            magEffects[i] = ROMData.GetByte(effectBase + WizardCollectables[Towns.STRICT_SPELL_LOCATIONS[i / 2]].VanillaSpellOrder() * 2);
            magEffects[i + 1] = ROMData.GetByte(effectBase + WizardCollectables[Towns.STRICT_SPELL_LOCATIONS[i / 2]].VanillaSpellOrder() * 2 + 1);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                magLevels[i, j] = ROMData.GetByte(spellCostBase + (WizardCollectables[Towns.STRICT_SPELL_LOCATIONS[i]].VanillaSpellOrder() * 8 + j));
            }

            for (int j = 0; j < 7; j++)
            {
                magNames[i, j] = ROMData.GetByte(spellNameBase + (WizardCollectables[Towns.STRICT_SPELL_LOCATIONS[i]].VanillaSpellOrder() * 0xe + j));
            }
        }
        */

        for (int i = 0; i < magFunction.Count(); i++)
        {
            ROMData.Put(functionBase + i, (byte)magFunction[i]);
        }

        for (int i = 0; i < magEffects.Count(); i = i + 2)
        {
            ROMData.Put(effectBase + i, (byte)magEffects[i]);
            ROMData.Put(effectBase + i + 1, (byte)magEffects[i + 1]);
        }

        for (int i = 0; i < 8; i++)
        {
            for (int j = 0; j < 8; j++)
            {
                ROMData.Put(spellCostBase + (i * 8) + j, (byte)magLevels[i, j]);
            }

            for (int j = 0; j < 7; j++)
            {
                ROMData.Put(spellNameBase + (i * 0xe) + j, (byte)magNames[i, j]);
            }
        }

        //fix for rope graphical glitch
        for (int i = 0; i < 16; i++)
        {
            ROMData.Put(0x32CD0 + i, ROMData.GetByte(0x34CD0 + i));
        }

        long inthash = BitConverter.ToInt64(hash, 0);

        ROMData.Put(0x17C2C, (byte)(((inthash) & 0x1F) + 0xD0));
        ROMData.Put(0x17C2E, (byte)(((inthash >> 5) & 0x1F) + 0xD0));
        ROMData.Put(0x17C30, (byte)(((inthash >> 10) & 0x1F) + 0xD0));
        ROMData.Put(0x17C32, (byte)(((inthash >> 15) & 0x1F) + 0xD0));
        ROMData.Put(0x17C34, (byte)(((inthash >> 20) & 0x1F) + 0xD0));
        ROMData.Put(0x17C36, (byte)(((inthash >> 25) & 0x1F) + 0xD0));
    }

    public void RandomizeSmallItems(int world, bool first)
    {
        logger.Debug("World: " + world);
        List<int> addresses = new List<int>();
        List<int> items = new List<int>();
        int startAddr;
        if (first)
        {
            startAddr = 0x8523 - 0x8000 + (world * 0x4000) + 0x10;
        }
        else
        {
            startAddr = 0xA000 - 0x8000 + (world * 0x4000) + 0x10;
        }
        int map = 0;
        for (int i = startAddr; i < startAddr + 126; i = i + 2)
        {

            map++;
            int low = ROMData.GetByte(i);
            int hi = ROMData.GetByte(i + 1) * 256;
            int numBytes = ROMData.GetByte(hi + low + 16 - 0x8000 + (world * 0x4000));
            for (int j = 4; j < numBytes; j = j + 2)
            {
                int yPos = ROMData.GetByte(hi + low + j + 16 - 0x8000 + (world * 0x4000)) & 0xF0;
                yPos = yPos >> 4;
                if (ROMData.GetByte(hi + low + j + 1 + 16 - 0x8000 + (world * 0x4000)) == 0x0F && yPos < 13)
                {
                    int addr = hi + low + j + 2 + 16 - 0x8000 + (world * 0x4000);
                    int item = ROMData.GetByte(addr);
                    if (item == 8 || (item > 9 && item < 14) || (item > 15 && item < 19) && !addresses.Contains(addr))
                    {
                        if (UNSAFE_DEBUG)
                        {
                            logger.Debug("Map: " + map);
                            logger.Debug("Item: " + item);
                            logger.Debug("Address: {0:X}", addr);
                        }
                        addresses.Add(addr);
                        items.Add(item);
                    }
                    j++;
                }
            }
        }

        for (int i = 0; i < items.Count; i++)
        {
            int swap = RNG.Next(i, items.Count);
            int temp = items[swap];
            items[swap] = items[i];
            items[i] = temp;
        }
        for (int i = 0; i < addresses.Count; i++)
        {
            ROMData.Put(addresses[i], (byte)items[i]);
        }
    }

    public void PrintSpoiler(LogLevel logLevel)
    {
        logger.Log(logLevel, "ITEMS:");
        foreach (Collectable item in Enum.GetValues(typeof(Collectable)))
        {
            if (item.IsMajorItem() && ItemGet.ContainsKey(item))
            {
                Location? location = AllLocationsForReal().Where(i => i.Collectable == item).FirstOrDefault();
                logger.Log(logLevel, item.ToString() + "(" + ItemGet[item] + ") : " + location?.Name);
            }
        }
    }

    /// <summary>
    /// Simple conversion routine that converts from a PRG rom address to CPU address.
    /// </summary>
    /// <param name="addr"></param>
    /// <returns></returns>
    private byte[] PrgToCpuAddr(int addr)
    {
        ushort banksize = 0x4000;
        byte bank = (byte)(addr / banksize);
        // If the bank is the last bank, then its the fixed bank and all pointers start from 0xc000 instead
        ushort cpuOffset = (ushort)(bank == 7 ? 0xc000 : 0x8000);
        ushort val = (ushort)(addr - (bank * banksize) + cpuOffset);
        return new byte[] { (byte)(val & 0xff), (byte)(val >> 8) };
    }

    private void AddCropGuideBoxesToFileSelect(Assembler a)
    {
        a.Module().Code("""
.segment "PRG5"
.org $b28d
    jsr CustomFileSelectUpdates

.reloc
CustomFileSelectUpdates:
    cpy #$01
    beq @Skip
        sta $0726  ; perform the original hooked code before exiting
        rts
@Skip:
    ldx #$20     ; copy 32 + 1 bytes of data from the rom (+1 for terminator)
@Loop:
        lda CustomFileSelectData,x
        sta $0302,x
        dex
        bpl @Loop    ; while x >= 0
    lda #0    ; use the draw handler for $302
    sta $0725 ; tell the code to draw from $302
    inc $073E ; increase the "state" count
    pla       ; we need to double return here to break out of the hook and the calling function
    pla
    rts

.define whiteTile $fd
.define blueTile $fe
CustomFileSelectData:
    .byte $20, $00, $01, whiteTile
    .byte $20, $1f, $01, whiteTile
    .byte $23, $a0, $01, whiteTile
    .byte $23, $bf, $01, whiteTile
    .byte $20, $21, $01, blueTile
    .byte $20, $3e, $01, blueTile
    .byte $23, $81, $01, blueTile
    .byte $23, $9e, $01, blueTile
    .byte $ff
""", "crop_guides.s");
    }

    public List<Location> AllLocationsForReal()
    {
        List<Location> locations = westHyrule.AllLocations
            .Union(eastHyrule.AllLocations)
            .Union(mazeIsland.AllLocations)
            .Union(deathMountain.AllLocations).ToList();
        return locations;
    }

    public string SpellDebug()
    {
        StringBuilder sb = new StringBuilder();
        Collectable c = westHyrule.AllLocations.First(i => i.ActualTown == Town.RAURU).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Rauru: " + Enum.GetName(typeof(Collectable), c));
        }
        c = westHyrule.AllLocations.First(i => i.ActualTown == Town.RUTO).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Ruto: " + Enum.GetName(typeof(Collectable), c));
        }
        c = westHyrule.AllLocations.First(i => i.ActualTown == Town.SARIA_NORTH).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Saria: " + Enum.GetName(typeof(Collectable), c));
        }
        c = westHyrule.AllLocations.First(i => i.ActualTown == Town.MIDO_WEST).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Mido West: " + Enum.GetName(typeof(Collectable), c));
        }
        c = westHyrule.AllLocations.First(i => i.ActualTown == Town.MIDO_CHURCH).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Mido Tower: " + Enum.GetName(typeof(Collectable), c));
        }
        c = eastHyrule.AllLocations.First(i => i.ActualTown == Town.NABOORU).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Nabooru: " + Enum.GetName(typeof(Collectable), c));
        }
        c = eastHyrule.AllLocations.First(i => i.ActualTown == Town.DARUNIA_WEST).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Darunia West: " + Enum.GetName(typeof(Collectable), c));
        }
        c = eastHyrule.AllLocations.First(i => i.ActualTown == Town.DARUNIA_ROOF).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Darunia Roof: " + Enum.GetName(typeof(Collectable), c));
        }
        c = eastHyrule.AllLocations.First(i => i.ActualTown == Town.NEW_KASUTO).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("New Kasuto: " + Enum.GetName(typeof(Collectable), c));
        }
        c = eastHyrule.AllLocations.First(i => i.ActualTown == Town.OLD_KASUTO).Collectable;
        if (!ItemGet[c])
        {
            sb.AppendLine("Old Kasuto: " + Enum.GetName(typeof(Collectable), c));
        }
        return sb.ToString();
    }

    public void PrintRoutingDebug(int count, int wh, int eh, int dm, int mi)
    {
        StringBuilder sb = new StringBuilder();
        sb.AppendLine("Number: " + debug);
        sb.AppendLine("Reached: " + count);
        sb.AppendLine("wh: " + wh + " / " + westHyrule.AllLocations.Count);
        sb.AppendLine("eh: " + eh + " / " + eastHyrule.AllLocations.Count);
        sb.AppendLine("dm: " + dm + " / " + deathMountain.AllLocations.Count);
        sb.AppendLine("mi: " + mi + " / " + mazeIsland.AllLocations.Count);
        sb.AppendLine("");

        //Debug.WriteLine("-" + count + "- " + accessibleMagicContainers);
        //SHUFFLABLE_STARTING_ITEMS.Where(i => ItemGet[item] == false).ToList().ForEach(i => Debug.WriteLine(Enum.GetName(typeof(Item), i)));
        List<Location> allLocations = AllLocationsForReal();
        foreach(Location location in allLocations.Where(i => !i.ItemGet && i.Collectable != Collectable.DO_NOT_USE))
        {
            sb.AppendLine(location.Name + " / " + Enum.GetName(typeof(Collectable), location.Collectable));
        }
        //sb.AppendLine(SpellDebug());
        //Debug.WriteLine("---Inaccessable Locations---");
        //allLocations.Where(i => !i.Reachable).ToList().ForEach(i => Debug.WriteLine(i.Name));


        sb.AppendLine("");
        logger.Error(sb.ToString());
    }

    private bool IsRaftAlwaysRequired(RandomizerProperties props)
    {
        //If continent connections are vanilla, you for sure need the raft.
        if (props.ContinentConnections == ContinentConnectionType.NORMAL)
        {
            return true;
        }
        //If west and DM are both vanilla, the caves have to connect between west and DM, and vanilla connections are forced
        if ((props.WestBiome == Biome.VANILLA || props.WestBiome == Biome.VANILLA_SHUFFLE)
            && (props.DmBiome == Biome.VANILLA || props.DmBiome == Biome.VANILLA_SHUFFLE))
        {
            return true;
        }
        //If east is vanilla, you can only get to it from raft or bridge, so if MI is vanilla, raft is required
        if ((props.EastBiome == Biome.VANILLA || props.EastBiome == Biome.VANILLA_SHUFFLE)
            && (props.MazeBiome == Biome.VANILLA || props.MazeBiome == Biome.VANILLA_SHUFFLE))
        {
            return true;
        }
        return false;
    }

    private void FixHelmetheadBossRoom(Assembler asm)
    {
        byte helmetRoom;
        if (props.PalaceStyles[1] == PalaceStyle.VANILLA)
        {
            helmetRoom = 0x22;
        }
        else
        {
            helmetRoom = (byte)palaces[1].BossRoom.Map;
        }

        var a = asm.Module();
        a.Assign("HelmetRoom", helmetRoom);
        a.Code("""
.segment "PRG4"

.reloc
HelmetHeadGoomaFix:
    lda #<HelmetRoom
    eor $561
    rts

.org $bac3
    jsr HelmetHeadGoomaFix
.org $bc83
    jsr HelmetHeadGoomaFix
.org $bd75
    jsr HelmetHeadGoomaFix

.org $9b27
; Also fix a key glitch
    nop
    nop
    nop

""", "helmethead_gooma_fix.s");
    }

    private void RestartWithPalaceUpA(Assembler a) {
        a.Module().Code("""
update_next_level_exp = $a057

;(0=caves, enemy encounters...; 1=west hyrule towns; 2=east hyrule towns; 3=palace 1,2,5 ; 4=palace 3,4,6 ; 5=great palace)
world_number = $707
room_code = $561
temp_room_code = $120
temp_room_flag = $121

.segment "PRG7"

.org $cbaa
    jsr PalacePatch

.reloc
PalacePatch:
    sta world_number
    ; if < 3 do the original patch
    cmp #$03
    bcc @Exit
    ; or if our temp flag is already set, do the original code
    lda temp_room_flag
    bne @Exit
        lda room_code
        sta temp_room_code ; store the area code into a temp ram location
        inc temp_room_flag ; set a flag in another empty ram location
@Exit:
    lda world_number
    rts

.reloc
ReloadExpForReset:
    lda temp_room_code
    sta room_code
    jsr update_next_level_exp
    lda world_number
    rts

.org $cad0
    jsr ReloadExpForReset ; Refresh the room marker and load the world number
    cmp #3                ; World 3 or greater (palaces)
    bcs $cade ; *+9       ; jump to restore/continue routine

.org $cae3
    ; Don't clear area code on reset
    nop
    nop
    nop

.org $cf92
    jsr SaveWorldStateAndClearFlag

.reloc
SaveWorldStateAndClearFlag:
    ; Precondition y = 0
    sty world_number
    sty temp_room_flag
    rts

""", "restart_palace_upa.s");
    }

    /// <summary>
    /// I assume this fixes the XP on screen transition softlock, but who knows with all these magic bytes.
    /// </summary>
    private void FixSoftLock(Assembler a)
    {
        a.Module().Code("""
.segment "PRG7"
.org $e18a
    jsr FixSoftlock

.reloc
FixSoftlock:
    inc $0726
    lda $074c ; branch if dialog type is not "talking"
    cmp #$02
    beq +
        ldx #$00  ; otherwise close the talking dialog
        stx $074c
+   rts
""", "fix_softlock.s");
    }

    public void ApplyHudFixes(Assembler asm, bool preventFlash)
    {
        var a = asm.Module();
        a.Assign("PREVENT_HUD_FLASH_ON_LAG", preventFlash ? 1 : 0);
        a.Code(Assembly.GetExecutingAssembly().ReadResource("RandomizerCore.Asm.FixedHud.s"), "fixed_hud.s");
    }
    
    public void ExpandedPauseMenu(Assembler a)
    {;
        a.Module().Code(Assembly.GetExecutingAssembly().ReadResource("RandomizerCore.Asm.ExpandedPauseMenu.s"), "expand_pause.s");
    }
    
    public void StandardizeDrops(Assembler a)
    {
        a.Module().Code("""
.segment "PRG7"
.org $e8ad
    jsr StandardizeDrops
.reloc
StandardizeDrops:
    cpy #$02
    bne +
        lda $06fe
        inc $06fe
        rts
+   lda $06ff
    inc $06ff
    rts
""", "standardize_drops.s");
    }

    public void PreventSideviewOutOfBounds(Assembler a)
    {
        a.Module().Code("""
; In vanilla, sideview loading routinely reads a few extra bytes past the end of sideview data,
; and if you are unlucky, it'll read a $dx byte which is valid and will make the room get blocked off
; This changes it so during background rendering, it'll properly check for bounds

.segment "PRG7"
.org $C78A ; Where the bg rendering reloads the data offset to get the next command byte
  jsr CheckIfEndOfData

.reloc
CheckIfEndOfData:
  ldy $072f ; load the current data offset
  cpy $072e ; if it is at the end of the sideview
  beq @EndOfData
    rts     ; otherwise do nothing
@EndOfData:
  pla       ; pop the return address so we can choose where to go back to
  pla
  lda #$4f  ; Set the cursor to the end of the sideview since we are outta data
  sta $010a
  jmp $c795 ; jump back to the background rendering code after where it loads the next byte
""", "prevent_sideview_oob.s");
    }

    public void FixContinentTransitions(Assembler asm)
    {
        var a = asm.Module();
        a.Assign("P1Palette", (byte)palPalettes[westHyrule?.locationAtPalace1.PalaceNumber ?? 0]);
        a.Assign("P2Palette", (byte)palPalettes[westHyrule?.locationAtPalace2.PalaceNumber ?? 1]);
        a.Assign("P3Palette", (byte)palPalettes[westHyrule?.locationAtPalace3.PalaceNumber ?? 2]);
        a.Assign("P4Palette", (byte)palPalettes[mazeIsland?.locationAtPalace4.PalaceNumber ?? 3]);
        a.Assign("P5Palette", (byte)palPalettes[eastHyrule?.locationAtPalace5.PalaceNumber ?? 4]);
        a.Assign("P6Palette", (byte)palPalettes[eastHyrule?.locationAtPalace6.PalaceNumber ?? 5]);
        a.Assign("PGreatPalette", (byte)palPalettes[eastHyrule?.locationAtGP.PalaceNumber ?? 6]);
        a.Code("""

.macpack common
.import SwapPRG

CurrentRegion = $0706
PRG_bank = $0769

.segment "PRG7"

; Patch switching the bank when loading the overworld
.org $cd48
    bne +
    ldy CurrentRegion
    lda ExpandedRegionBankTable,y
+   sta PRG_bank
    jsr SwapPRG
    beq $cd5f ; unconditional jmp to skip the freespace
FREE_UNTIL $cd5f

.org $c506
    tay
    lda RaftWorldMappingTable,y
    asl a
    tay

.org $cd84
    tay
    lda RaftWorldMappingTable,y
    asl a
    tay

.org $ce32
    lda PalacePaletteOffset,y

.reloc
ExpandedRegionBankTable:
    .byte $01, $01, $02, $02
PalacePaletteOffset:
    .byte P1Palette, P2Palette, P3Palette, $20, $30, $30, $30, $30, P5Palette, P6Palette, PGreatPalette, $60, P4Palette
.reloc
RaftWorldMappingTable:
    .byte $00, $02, $00, $02
.export RaftWorldMappingTable

.org $C265
; Change the pointer table for Item presence to include only the low byte
; Since the high byte is always $06
bank7_Pointer_table_for_Item_Presence:
    .byte .lobyte($0600)
    .byte .lobyte($0660)
    .byte .lobyte($0660)
    .byte .lobyte($0680)
    .byte .lobyte($06A0)
    .byte .lobyte($0620)
    .byte .lobyte($0660)
    .byte .lobyte($0660)
    .byte .lobyte($0680)
    .byte .lobyte($06A0)
    .byte .lobyte($0640)
    .byte .lobyte($0660)
    .byte .lobyte($0660)
    .byte .lobyte($0680)
    .byte .lobyte($06A0)
    .byte .lobyte($06C0)
; Add these new pointers for item presence as well
    .byte .lobyte($0660)
    .byte .lobyte($0660)
    .byte .lobyte($0680)
    .byte .lobyte($06A0)
    .byte .lobyte($06C0)
FREE_UNTIL $C285

; Patch loading from the table to use the new address
.org $c2b6
    tay
    lda bank7_Pointer_table_for_Item_Presence,y
    sta $00
    lda #06
    sta $01
    lda $0561
    lsr a
    tay
    lda ($00),y
    rts
FREE_UNTIL $c2ca

; Remove vanilla check to see if you are in east hyrule when using the raft
.segment "PRG0"
.org $85a2
    nop
    nop

""", "fix_continent_transitions.s");
    }

    public void UpdateHints(Assembler asm, List<Text> hints)
    {
        var a = asm.Module();
        // Clear out the ROM for the existing tables
        a.Free("PRG3", 0xA380, 0xB082);

        // Update the pointers to the text tables
        a.Segment("PRG3");
        a.Org(0xB423);
        a.Word(a.Symbol("Towns_in_West_Hyrule"));
        a.Word(a.Symbol("Towns_in_East_Hyrule"));

        for (var i = 0; i < hints.Count; i++) {
            var hint = hints[i];
            a.Reloc();
            a.Label($"HintText{i}");
            a.Byt(hint.TextChars.Select(c => (byte)c).ToArray());
        }

        a.Reloc();
        a.Label("Towns_in_West_Hyrule");
        // There are 52 texts in this first table
        for (var i = 0; i < 52; i++) {
            var hint = hints[i];
            a.Word(a.Symbol($"HintText{i}"));
        }
        // and the rest are in this table
        a.Reloc();
        a.Label("Towns_in_East_Hyrule");
        for (var i = 52; i < hints.Count; i++) {
            var hint = hints[i];
            a.Word(a.Symbol($"HintText{i}"));
        }
    }

    private void ApplyAsmPatches(RandomizerProperties props, Assembler engine, Random RNG, ROM rom)
    {
        rom.ChangeMapperToMMC5(engine);
        AddCropGuideBoxesToFileSelect(engine);
        FixHelmetheadBossRoom(engine);

        if (props.RandomizeKnockback)
        {
            rom.RandomizeKnockback(engine, RNG);
        }

        if (props.HardBosses)
        {
            rom.BuffCarrock(engine);
        }

        if (props.ReplaceFireWithDash)
        {
            rom.DashSpell(engine);
        }

        if (props.UpAC1)
        {
            rom.UpAController1(engine);
        }
        
        if (props.UpARestartsAtPalaces)
        {
            RestartWithPalaceUpA(engine);
        }

        if (props.StandardizeDrops)
        {
            StandardizeDrops(engine);
        }
        FixSoftLock(engine);
        ApplyHudFixes(engine, props.DisableHUDLag);
        RandomizeStartingValues(engine, rom);
        rom.ExtendMapSize(engine);
        ExpandedPauseMenu(engine);
        FixContinentTransitions(engine);
        PreventSideviewOutOfBounds(engine);

        UpdateHints(engine, hints);
    }

    //This entire town location shuffle structure is awful if this method needs to exist.
    private Location GetTownLocation(Town town)
    {
        if(westHyrule.locationAtMido.ActualTown == town)
        {
            return westHyrule.locationAtMido;
        }
        if (westHyrule.locationAtRauru.ActualTown == town)
        {
            return westHyrule.locationAtRauru;
        }
        if (westHyrule.locationAtRuto.ActualTown == town)
        {
            return westHyrule.locationAtRuto;
        }
        if (westHyrule.locationAtSariaNorth.ActualTown == town)
        {
            return westHyrule.locationAtSariaNorth;
        }
        if (eastHyrule.townAtDarunia.ActualTown == town)
        {
            return eastHyrule.townAtDarunia;
        }
        if (eastHyrule.townAtNabooru.ActualTown == town)
        {
            return eastHyrule.townAtNabooru;
        }
        if (eastHyrule.townAtNewKasuto.ActualTown == town)
        {
            return eastHyrule.townAtNewKasuto;
        }
        if (eastHyrule.townAtOldKasuto.ActualTown == town)
        {
            return eastHyrule.townAtOldKasuto;
        }
        throw new Exception("Unrecognized town in GetTownLocation, was this extended to non-standard wizards?");
    }
}
