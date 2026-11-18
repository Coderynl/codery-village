using Codery.Village.Core.Map;

namespace Codery.Village.Tests;

public class HexTests
{
    [Fact]
    public void Every_cell_has_six_distinct_neighbours()
    {
        foreach (var cell in new[] { new Cell(4, 4), new Cell(5, 4) })
        {
            var neighbours = cell.Neighbours().ToList();
            Assert.Equal(6, neighbours.Distinct().Count());
            Assert.DoesNotContain(cell, neighbours);
        }
    }

    [Fact]
    public void Being_a_neighbour_goes_both_ways_on_even_and_odd_rows()
    {
        for (var row = 2; row < 6; row++)
        for (var col = 2; col < 6; col++)
        {
            var cell = new Cell(row, col);
            foreach (var neighbour in cell.Neighbours())
                Assert.Contains(cell, neighbour.Neighbours());
        }
    }

    [Fact]
    public void Odd_rows_are_shifted_half_a_tile_to_the_right()
    {
        // the same layout as the level in the browser: the tile south-east of an even row keeps its column
        Assert.Contains(new Cell(5, 4), new Cell(4, 4).Neighbours());
        Assert.Contains(new Cell(5, 3), new Cell(4, 4).Neighbours());
        Assert.Contains(new Cell(6, 5), new Cell(5, 4).Neighbours());
        Assert.Contains(new Cell(6, 4), new Cell(5, 4).Neighbours());
    }

    [Theory]
    [InlineData(4, 4, 4, 7, 3)]
    [InlineData(4, 4, 6, 4, 2)]
    [InlineData(3, 2, 7, 6, 6)]
    public void Distance_counts_the_steps_between_two_cells(int row, int col, int toRow, int toCol, int steps)
    {
        Assert.Equal(steps, new Cell(row, col).DistanceTo(new Cell(toRow, toCol)));
    }
}
