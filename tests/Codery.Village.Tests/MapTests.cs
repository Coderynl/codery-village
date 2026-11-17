using Codery.Village.Core.Map;

namespace Codery.Village.Tests;

public class MapTests
{
    // three separate pieces of land: two on the left and right, one in the middle
    private const string ThreePieces = """
        # a small map for the tests
        ~~~~~~~~~~~
        ~..~~~~~TT~
        ~h.~~F~~RC~
        ~..~~~~~vv~
        ~~~~~~~~~~~
        island 1 1 Links
        island 2 5 Midden eddie
        island 2 8 Rechts
        """;

    [Fact]
    public void Islands_are_found_by_flood_fill()
    {
        var map = MapText.Parse(ThreePieces);

        Assert.Equal(5, map.Rows);
        Assert.Equal(11, map.Cols);
        Assert.Equal(new[] { "Links", "Midden", "Rechts" }, map.Islands.Select(island => island.Name).Order());
        Assert.Null(map.IslandOf(new Cell(0, 0)));
        Assert.Null(map.IslandOf(new Cell(3, 8)));

        var left = map.Islands.Single(island => island.Name == "Links");
        Assert.Equal(6, left.Cells.Count);
        Assert.All(left.Cells, cell => Assert.Equal(left.Id, map.IslandOf(cell)));
        Assert.False(left.Eddie);
        Assert.True(map.Islands.Single(island => island.Name == "Midden").Eddie);
    }

    [Fact]
    public void Each_character_is_its_own_ground()
    {
        var map = MapText.Parse(ThreePieces);

        Assert.Equal(Ground.Water, map.At(new Cell(0, 0)));
        Assert.Equal(Ground.Land, map.At(new Cell(1, 1)));
        Assert.Equal(Ground.Lot, map.At(new Cell(2, 1)));
        Assert.Equal(Ground.Fertile, map.At(new Cell(2, 5)));
        Assert.Equal(Ground.Forest, map.At(new Cell(1, 8)));
        Assert.Equal(Ground.Rock, map.At(new Cell(2, 8)));
        Assert.Equal(Ground.Clay, map.At(new Cell(2, 9)));
        Assert.Equal(Ground.Fishing, map.At(new Cell(3, 8)));
        Assert.Equal(Ground.Water, map.At(new Cell(-1, 4)));
        Assert.False(map.IsWalkable(new Cell(3, 8)));
        Assert.False(map.IsWalkable(new Cell(99, 99)));
        Assert.True(map.IsWalkable(new Cell(2, 1)));
    }

    [Fact]
    public void A_route_never_crosses_water()
    {
        var map = MapText.Parse(ThreePieces);

        Assert.Null(map.FindRoute(new Cell(1, 1), new Cell(2, 8)));
        Assert.Null(map.FindRoute(new Cell(1, 1), new Cell(0, 0)));

        var route = map.FindRoute(new Cell(1, 1), new Cell(3, 2));
        Assert.NotNull(route);
        Assert.Equal(new Cell(3, 2), route.Cells[^1]);
        Assert.All(route.Cells, cell => Assert.True(map.IsWalkable(cell)));
        var previous = new Cell(1, 1);
        foreach (var cell in route.Cells)
        {
            Assert.Contains(cell, previous.Neighbours());
            previous = cell;
        }
        Assert.Equal(new Cell(1, 1).DistanceTo(new Cell(3, 2)), route.Cells.Count);
    }

    [Theory]
    [InlineData("~~\n~x\n", 2)]
    [InlineData("~~\n~\n", 2)]
    [InlineData("~.\nisland 0 0 Zee\n", 2)]
    [InlineData("~.\nisland 0 1\n", 2)]
    [InlineData("~.\nisland 0 1 Een\nisland 0 1 Twee\n", 3)]
    [InlineData("~.\nisland 0 1 Een vreemd\n", 2)]
    [InlineData("~.~.\nisland 0 1 Een\n", 1)]
    [InlineData("~.\nisland 0 1 Een\n~.\n", 3)]
    public void A_mistake_names_its_line(string text, int line)
    {
        var error = Assert.Throws<FormatException>(() => MapText.Parse(text));
        Assert.Contains($"line {line}", error.Message);
    }

    [Fact]
    public void A_shape_covers_its_cells_in_every_rotation()
    {
        var pair = new Shape([new Cell(0, 0), new Cell(0, 1)]);

        foreach (var anchor in new[] { new Cell(4, 4), new Cell(5, 4) })
        {
            var seen = new HashSet<Cell>();
            for (var rotation = 0; rotation < 6; rotation++)
            {
                var cells = pair.Cover(anchor, rotation).ToList();
                Assert.Equal(2, cells.Count);
                Assert.Equal(anchor, cells[0]);
                Assert.Contains(cells[1], anchor.Neighbours());
                seen.Add(cells[1]);
            }
            Assert.Equal(6, seen.Count);
            Assert.Equal(pair.Cover(anchor, 0), pair.Cover(anchor, 6));
        }
        Assert.Equal(new[] { new Cell(5, 4) }, Shape.Single.Cover(new Cell(5, 4), 3));
    }

    [Fact]
    public void The_village_map_has_two_islands_and_Eddies_islet()
    {
        var map = MapText.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "data", "map.txt")));

        Assert.Equal(3, map.Islands.Count);
        var eddie = Assert.Single(map.Islands, island => island.Eddie);
        Assert.Equal("Eddie", eddie.Name);
        Assert.InRange(eddie.Cells.Count, 3, 5);

        var west = map.Islands.Single(island => island.Name == "West");
        var east = map.Islands.Single(island => island.Name == "Oost");
        Assert.True(west.Cells.Max(cell => cell.Col) < eddie.Cells.Min(cell => cell.Col));
        Assert.True(eddie.Cells.Max(cell => cell.Col) < east.Cells.Min(cell => cell.Col));

        var westGround = west.Cells.Select(map.At).ToHashSet();
        Assert.Contains(Ground.Forest, westGround);
        Assert.Contains(Ground.Fertile, westGround);
        var eastGround = east.Cells.Select(map.At).ToHashSet();
        Assert.Contains(Ground.Rock, eastGround);
        Assert.Contains(Ground.Clay, eastGround);
        Assert.Contains(east.Cells.SelectMany(cell => cell.Neighbours()), cell => map.At(cell) == Ground.Fishing);

        foreach (var island in new[] { west, east })
            Assert.True(island.Cells.Count(cell => map.At(cell) == Ground.Lot) >= 20, $"{island.Name} has too few lots");
    }
}
