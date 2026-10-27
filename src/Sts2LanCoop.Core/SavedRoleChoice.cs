namespace Sts2LanCoop.Core;

// Selection uses persistent native slots, never display names or character type as identity.
public static class SavedRoleChoice
{
    public static void Validate(AdmissionMessage room)
    {
        if (!room.Resume || string.IsNullOrWhiteSpace(room.Session) || string.IsNullOrWhiteSpace(room.Revision) ||
            room.Slots == null || room.Occupied == null || room.Names == null ||
            room.Count is < 2 or > 4 || room.Slots.Count != room.Count || room.Slots.Count(s => s.NativeId == 1) != 1 ||
            room.Slots.Any(s => s.NativeId == 0) || room.Slots.Select(s => s.NativeId).Distinct().Count() != room.Count)
            throw new InvalidDataException("房间的存档角色资料无效，请重新查看房间。");
    }
    public static bool SameAdventure(AdmissionMessage expected, AdmissionMessage actual) =>
        actual.Resume && expected.Session == actual.Session && expected.Revision == actual.Revision &&
        expected.Count == actual.Count && expected.Slots.Select(s => s.NativeId).Order().SequenceEqual(actual.Slots.Select(s => s.NativeId).Order());
    public static bool CanSelect(AdmissionMessage room, ulong slot) => !room.Running && slot != 1 &&
        room.Slots.Any(s => s.NativeId == slot) && !room.Occupied.Contains(slot.ToString());
}
