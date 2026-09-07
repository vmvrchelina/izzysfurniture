using System;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.Game.Character;
using FFXIVClientStructs.FFXIV.Client.Game.Object;

namespace IzzysFurniture;

internal static unsafe class NpcActorIdentity
{
    public static void Initialize(Character* character, ushort homeWorld)
    {
        character->ObjectKind = ObjectKind.Pc;
        character->OwnerId = 0xE0000000;
        character->BaseId = 0;
        character->NameId = 0;
        character->HomeWorld = homeWorld;
        character->TargetableStatus &= ~ObjectTargetableFlags.IsTargetable;
        character->NamePlateIconId = 0;

        var name = Name(character->ObjectIndex);
        character->Name.Clear();
        name.CopyTo(character->Name);
    }

    public static byte[] Name(ushort objectIndex)
    {
        Span<char> suffix = stackalloc char[4];
        var value = objectIndex;
        for (var index = suffix.Length - 1; index >= 0; index--)
        {
            suffix[index] = (char)('a' + value % 26);
            value /= 26;
        }

        return Encoding.UTF8.GetBytes($"Izzy Actor{new string(suffix)}");
    }
}
