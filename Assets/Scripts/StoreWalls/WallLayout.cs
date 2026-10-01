using System;
using System.Collections.Generic;
using UnityEngine;

// Same order as RoomStructure's WallPropSettings array.
public enum WallSide { Right, Left, Front, Back }
public enum ExitDoorType { DoorG, DoorH, DoorI }
public enum ExitDoorPosition { Left, Middle, Right }

/// <summary>Which wall faces the street, how its exit door is built and the seed of the buildings around it. Left/Right are as seen from the street.</summary>
[Serializable]
public class StreetSettings
{
    public WallSide wall = WallSide.Front;
    public ExitDoorType door = ExitDoorType.DoorG;
    public ExitDoorPosition position = ExitDoorPosition.Middle;
    // 0 = unset: a blank builder store rolls one, older saves derive it from the store name (see SeedFor).
    public int seed;

    public StreetSettings Clone() => (StreetSettings)MemberwiseClone();

    public void Reroll() => seed = new System.Random().Next(1, int.MaxValue);

    /// <summary>Stable across runs and platforms (string.GetHashCode is not): FNV-1a of the store name, never 0.</summary>
    public static int SeedFor(string storeName)
    {
        uint hash = 2166136261;
        foreach (char c in storeName ?? string.Empty) hash = (hash ^ c) * 16777619;
        return hash == 0 ? 1 : (int)hash;
    }
}

public enum WallPiece
{
    Blank1, Blank2, Blank3,
    WindowA2, WindowA3,
    WindowE,         // opening reaches its left end (start corner)
    WindowEFlipped,  // opening reaches its right end (far corner)
    ExitDoorG, ExitDoorH, ExitDoorI,
    EmergencyDoor    // 1 m door hole, placed on a cell of a non-street wall
}

public readonly struct WallPieceInfo
{
    public readonly string prefab;   // under Resources/LoafbrrAssets/Interiors_A/prefabs/
    public readonly float width;
    public readonly string door;     // door prefab spawned inside the hole, if any
    public readonly float openMinY;  // height range the piece is see-through / walkable (props avoid it)
    public readonly float openMaxY;
    public readonly float openMinX;  // opening's range along the piece at eye height, from its centre (measured from the meshes)
    public readonly float openMaxX;

    public WallPieceInfo(string prefab, float width, string door = null, float openMinY = 0f, float openMaxY = 0f,
        float openMinX = 0f, float openMaxX = 0f)
    {
        this.prefab = prefab;
        this.width = width;
        this.door = door;
        this.openMinY = openMinY;
        this.openMaxY = openMaxY;
        this.openMinX = openMinX;
        this.openMaxX = openMaxX;
    }

    public bool HasOpening => openMaxY > openMinY;
}

public static class WallPieces
{
    const float SillY = 0.75f, LintelY = 2.25f, DoorTopY = 2.12f;

    // A window edge is open when its opening reaches the piece end: against plain wall the hollow wall end shows as a
    // see-through strip, so it only fits another open edge or a corner post. E is open left, E_Flipped right (as are
    // the 2 m F / F_Flipped); G, H, I are open both sides and only chain with them (unused). A, B, C, D and doors are closed.
    static readonly Dictionary<WallPiece, WallPieceInfo> Catalog = new()
    {
        [WallPiece.Blank1] = new("wall/Wall_1m", 1f),
        [WallPiece.Blank2] = new("wall/Wall_2m", 2f),
        [WallPiece.Blank3] = new("wall/Wall_3m", 3f),
        [WallPiece.WindowA2] = Window("wall/Wall_2m_Window_A", 2f, -0.44f, 0.44f),
        [WallPiece.WindowA3] = Window("wall/Wall_3m_Window_A", 3f, -0.87f, 0.87f),
        [WallPiece.WindowE] = Window("wall/Wall_3m_Window_E", 3f, -1.5f, 0.87f),
        [WallPiece.WindowEFlipped] = Window("wall/Wall_3m_Window_E_Fliiped", 3f, -0.87f, 1.5f),
        // Hole widths (measured): Door_B 1.96 -> Door_G, 2m Door_B 1.26 -> Door_H, 2m Door_A 0.74 -> Door_I.
        [WallPiece.ExitDoorG] = ExitDoor("wall/Wall_3m_Door_B", 3f, "Door/Door_G_Grp", 0.98f),
        [WallPiece.ExitDoorH] = ExitDoor("wall/Wall_2m_Door_B", 2f, "Door/Door_H_Grp", 0.63f),
        [WallPiece.ExitDoorI] = ExitDoor("wall/Wall_2m_Door_A", 2f, "Door/Door_I_Grp", 0.37f),
        [WallPiece.EmergencyDoor] = ExitDoor("wall/Wall_1m_Door", 1f, "Door/Door_F_Grp", 0.37f),
    };

    static WallPieceInfo Window(string prefab, float width, float openMinX, float openMaxX) =>
        new(prefab, width, null, SillY, LintelY, openMinX, openMaxX);

    static WallPieceInfo ExitDoor(string prefab, float width, string door, float halfHole) =>
        new(prefab, width, door, 0f, DoorTopY, -halfHole, halfHole);

    public static WallPieceInfo Info(this WallPiece piece) => Catalog[piece];
    public static bool IsBlank(this WallPiece piece) => piece <= WallPiece.Blank3;
}

public static class WallSides
{
    public static readonly WallSide[] All = { WallSide.Right, WallSide.Left, WallSide.Front, WallSide.Back };

    public static Vector3 Normal(this WallSide side) => side switch
    {
        WallSide.Right => Vector3.right,
        WallSide.Left => Vector3.left,
        WallSide.Front => Vector3.forward,
        _ => Vector3.back
    };
}

/// <summary>A wall seen from outside: origin is its left end on the inner face at floor level.</summary>
public readonly struct WallFrame
{
    public readonly Vector3 origin;
    public readonly Vector3 right;   // along the wall, left -> right as seen from outside
    public readonly Vector3 normal;  // outward
    public readonly float length;

    public WallFrame(Vector3 origin, Vector3 right, Vector3 normal, float length)
    {
        this.origin = origin;
        this.right = right;
        this.normal = normal;
        this.length = length;
    }

    // Piece local +X runs along `right`, local +Z points into the store.
    public Quaternion Rotation => Quaternion.LookRotation(-normal, Vector3.up);

    public Vector3 Point(float along, float outward = 0f) => origin + right * along + normal * outward;
}

/// <summary>One 1 m cell along a wall, counted from its left end as seen from outside (emergency exits are saved as these).</summary>
[Serializable]
public struct WallCell
{
    public WallSide side;
    public int cell;
}

/// <summary>One piece position along a wall. Positions/lengths are metres along the wall's <see cref="WallFrame"/>.</summary>
public class WallSlot
{
    public WallSide side;
    public int index;          // position in the side's slot list, left to right
    public float start;
    public float width;        // may differ from the piece's native width (blank fillers are scaled in X)
    public int firstCell;      // first 1 m grid cell covered (the grid is what emergency exits snap to)
    public int cellCount;
    public WallPiece piece;
    public Vector3 position;   // world pose of the piece pivot (bottom centre of the wall thickness)
    public Quaternion rotation;
    public GameObject go;
}

/// <summary>Pure, deterministic wall layouts. Never throws: too-short walls degrade to blank pieces.</summary>
public static class WallLayout
{
    public const float Thickness = 0.2f;
    public const float RowHeight = 3f;     // native height of every piece
    public const float MinWallLength = 4f; // smallest store side, whatever the door
    public const int ExitCornerCells = 1;  // whole cells of wall kept between an emergency exit and each corner
    const float SightInset = 0.5f;     // closest an eye gets to a wall (inside distance and distance to the side walls)
    const float MaxSightReach = 1000f; // the cameras' far clip: nothing further is drawn anyway
    const float Eps = 1e-3f;

    struct Span
    {
        public WallPiece piece;
        public float width;
    }

    public static WallPiece ExitDoorPiece(ExitDoorType door) => door switch
    {
        ExitDoorType.DoorG => WallPiece.ExitDoorG,
        ExitDoorType.DoorH => WallPiece.ExitDoorH,
        _ => WallPiece.ExitDoorI
    };

    /// <summary>Whole 1 m cells of a wall; the fractional rest sits at its far end.</summary>
    public static int WholeCells(float length) => Mathf.Max(0, Mathf.FloorToInt(length + Eps));

    /// <summary>True when a 1 m emergency exit fits on this cell of a wall of the given length (clear of the corners).</summary>
    public static bool IsExitCell(float length, int cell) =>
        cell >= ExitCornerCells && cell < WholeCells(length) - ExitCornerCells;

    /// <summary>
    /// Shortest street wall (metres) that fits the door and the smallest window next to it (a centred door
    /// splits the rest in whole metres, rounding up, so it needs one more metre), never below <see cref="MinWallLength"/>.
    /// </summary>
    public static float MinStreetLength(ExitDoorType door, ExitDoorPosition position)
    {
        float window = WallPiece.WindowA2.Info().width + (position == ExitDoorPosition.Middle ? 1f : 0f);
        return Mathf.Max(MinWallLength, Mathf.Ceil((ExitDoorPiece(door).Info().width + window) * 10f) / 10f);
    }

    /// <summary>
    /// Metres the road and far row must reach past each end of the street wall so no sightline through an opening
    /// ends at them. The steepest ray through an opening [a1, a2] leaves the outer face at a2, from an eye SightInset
    /// off both walls: its slope (along per outward metre) is min(width / thickness, (a2 - SightInset) / (SightInset +
    /// thickness)), and it then covers rowFront - thickness. Wider openings and longer walls see further.
    /// </summary>
    /// <param name="street">Slots of the street wall.</param>
    /// <param name="rowFront">Outward distance from the inner face to the far row's fronts.</param>
    public static float SightReach(IReadOnlyList<WallSlot> street, float length, float rowFront)
    {
        float reach = 0f, travel = rowFront - Thickness;
        foreach (WallSlot slot in street)
        {
            WallPieceInfo info = slot.piece.Info();
            float centre = slot.start + slot.width * 0.5f;
            float a1 = centre + info.openMinX, a2 = centre + info.openMaxX;
            if (a2 - a1 <= Eps) continue;

            reach = Mathf.Max(reach, a2 - length + SightSlope(a2 - a1, a2 - SightInset) * travel);   // looking towards the far end
            reach = Mathf.Max(reach, -a1 + SightSlope(a2 - a1, length - a1 - SightInset) * travel);  // looking towards the start
        }

        return Mathf.Min(reach, MaxSightReach);
    }

    static float SightSlope(float openingWidth, float eyeToEdge) =>
        Mathf.Max(0f, Mathf.Min(openingWidth / Thickness, eyeToEdge / (SightInset + Thickness)));

    /// <summary>
    /// Street wall: [left segment][exit door][right segment]. The door sits at either end or within 0.5 m of
    /// the centre: the left segment is whole metres, so only the right one can hold the fractional leftover
    /// (at most one scaled piece per wall). Each segment is filled from the door towards its corner.
    /// </summary>
    public static List<WallSlot> Street(WallSide side, float length, StreetSettings street)
    {
        WallPiece door = ExitDoorPiece(street.door);
        float doorWidth = door.Info().width;
        float rest = length - doorWidth;
        if (rest < 0f) return Plain(side, length);

        float left = street.position switch
        {
            ExitDoorPosition.Left => 0f,
            ExitDoorPosition.Right => rest,
            _ => Mathf.Floor(rest * 0.5f + 0.5f + Eps)   // nearest whole metre to the centre
        };

        List<Span> spans = FillSegment(left, cornerAtStart: true);
        spans.Add(new Span { piece = door, width = doorWidth });
        spans.AddRange(FillSegment(rest - left, cornerAtStart: false));
        return ToSlots(side, spans, length);
    }

    /// <summary>
    /// Blank wall on a 1 m cell grid. Cells listed in <paramref name="cellOverrides"/> become that (1 m) piece;
    /// runs of free cells merge into 3/2/1 m blanks. The fractional cell sits at the far end and stretches
    /// the last blank (or becomes one scaled blank), so cell indices never shift.
    /// </summary>
    public static List<WallSlot> Plain(WallSide side, float length, IReadOnlyDictionary<int, WallPiece> cellOverrides = null)
    {
        var spans = new List<Span>();
        int cells = WholeCells(length);
        float fraction = Mathf.Max(0f, length - cells);

        int run = 0;
        for (int cell = 0; cell < cells; cell++)
        {
            if (cellOverrides != null && cellOverrides.TryGetValue(cell, out WallPiece piece))
            {
                AddBlankRun(spans, run);
                run = 0;
                spans.Add(new Span { piece = piece, width = piece.Info().width });
            }
            else run++;
        }
        AddBlankRun(spans, run);

        if (fraction > Eps)
        {
            if (spans.Count > 0 && spans[^1].piece.IsBlank())
                spans[^1] = new Span { piece = spans[^1].piece, width = spans[^1].width + fraction };
            else
                spans.Add(new Span { piece = WallPiece.Blank1, width = fraction });
        }

        return ToSlots(side, spans, length);
    }

    static void AddBlankRun(List<Span> spans, int cells)
    {
        for (; cells >= 3; cells -= 3) spans.Add(new Span { piece = WallPiece.Blank3, width = 3f });
        if (cells == 2) spans.Add(new Span { piece = WallPiece.Blank2, width = 2f });
        else if (cells == 1) spans.Add(new Span { piece = WallPiece.Blank1, width = 1f });
    }

    /// <summary>
    /// Fills the wall between the exit door and a corner, windows first, with closed window edges only (E's open
    /// edge sits against the corner post):
    /// - 3 m or more: a corner window (E), closed windows, and the leftover fraction as one blank by the door;
    /// - 2 m up to 3 m: one closed window, then a blank (the fraction) at the corner;
    /// - shorter: one blank.
    /// Every window keeps at least 0.5 m of its own wall from the corner.
    /// </summary>
    static List<Span> FillSegment(float length, bool cornerAtStart)
    {
        var spans = new List<Span>();   // door -> corner order
        if (length >= 3f)
        {
            int whole = Mathf.FloorToInt(length + Eps);
            int meters = whole - 3;   // whole metres between the door and the corner window
            float blank = length - whole + (meters == 1 ? 1f : 0f);   // a lone metre is too small for a closed window
            if (blank > Eps) spans.Add(Blank(blank));
            AddWindows(spans, meters);
            spans.Add(new Span { piece = cornerAtStart ? WallPiece.WindowE : WallPiece.WindowEFlipped, width = 3f });
        }
        else if (length >= 2f)
        {
            AddWindows(spans, 2);
            if (length - 2f > Eps) spans.Add(Blank(length - 2f));
        }
        else if (length > Eps) spans.Add(Blank(length));

        if (cornerAtStart) spans.Reverse();
        return spans;
    }

    // Whole metres of closed windows: 3 m ones, with one or two 2 m ones for the remainder (a single metre stays blank).
    static void AddWindows(List<Span> spans, int meters)
    {
        if (meters < 2) return;
        int small = meters % 3 == 1 ? 2 : meters % 3 == 2 ? 1 : 0;
        for (int rest = meters - small * 2; rest > 0; rest -= 3) spans.Add(new Span { piece = WallPiece.WindowA3, width = 3f });
        for (int i = 0; i < small; i++) spans.Add(new Span { piece = WallPiece.WindowA2, width = 2f });
    }

    // Blank of any width; the closest native blank gets scaled to it.
    static Span Blank(float width) => new()
    {
        piece = width < 1.5f ? WallPiece.Blank1 : width < 2.5f ? WallPiece.Blank2 : WallPiece.Blank3,
        width = width
    };

    static List<WallSlot> ToSlots(WallSide side, List<Span> spans, float length)
    {
        var slots = new List<WallSlot>(spans.Count);
        float start = 0f;
        foreach (Span span in spans)
        {
            int firstCell = Mathf.FloorToInt(start + Eps);
            slots.Add(new WallSlot
            {
                side = side,
                index = slots.Count,
                start = start,
                width = span.width,
                firstCell = firstCell,
                cellCount = Mathf.Max(1, Mathf.CeilToInt(start + span.width - Eps) - firstCell),
                piece = span.piece
            });
            start += span.width;
        }

        Debug.Assert(Mathf.Abs(start - length) < Eps * 10f, $"{side} wall layout covers {start} m of {length} m");
        return slots;
    }
}
