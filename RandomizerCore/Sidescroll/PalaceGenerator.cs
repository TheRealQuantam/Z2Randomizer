﻿using System;

namespace RandomizerCore.Sidescroll;

public abstract class PalaceGenerator
{
    protected const int ROOM_SHUFFLE_ATTEMPT_LIMIT = 100;
    protected const int DROP_PLACEMENT_FAILURE_LIMIT = 100;
    protected const int ROOM_PLACEMENT_FAILURE_LIMIT = 100;

    internal abstract Palace GeneratePalace(RandomizerProperties props, RoomPool rooms, Random r, int roomCount, int palaceNumber);

}
