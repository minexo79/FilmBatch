using System.Globalization;
using FilmBatch.App.Controls;

namespace FilmBatch.App.Tests;

public class AspectHeightConverterTests
{
    [Theory]
    [InlineData(120.0, 80.0)]
    [InlineData(0.0, 0.0)]
    public void ThreeByTwo_HeightIsTwoThirdsOfWidth(double width, double expected) =>
        Assert.Equal(expected, (double)AspectHeightConverter.ThreeByTwo.Convert(width, typeof(double), null, CultureInfo.InvariantCulture), 6);

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData("x")]
    public void NonFiniteOrWrongTypeGivesZero(object input) =>
        Assert.Equal(0.0, AspectHeightConverter.ThreeByTwo.Convert(input, typeof(double), null, CultureInfo.InvariantCulture));
}
