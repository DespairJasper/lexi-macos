using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.VisualTree;

namespace Lexi;

public partial class MainWindow
{
    private readonly ConditionalWeakTable<Border, CardDimensions> _cardDimensions = new();
    private double _cardWidthRatio = 1;
    private double _cardHeightRatio = 1;

    private sealed class CardDimensions(Border card)
    {
        public double Width { get; } = card.Width;
        public double MaxWidth { get; } = card.MaxWidth;
        public double MaxHeight { get; } = card.MaxHeight;
        public double BaseHeight { get; set; }
    }

    private void ConfigureResponsiveCards()
    {
        // Capture each card once it has an actual layout. Weak keys allow rows
        // from previous vocabulary pages to be collected normally.
        LayoutUpdated += (_, _) => ApplyResponsiveCardDimensions();
    }

    private void ApplyResponsiveCardDimensions()
    {
        foreach (var card in this.GetVisualDescendants().OfType<Border>().Where(x => x.Classes.Contains("card")))
        {
            if (_wordFocusActive && card == LookupResultCard) continue;
            var dimensions = _cardDimensions.GetValue(card, x => new CardDimensions(x));
            if (double.IsFinite(dimensions.Width)) card.Width = dimensions.Width * _cardWidthRatio;
            if (double.IsFinite(dimensions.MaxWidth)) card.MaxWidth = dimensions.MaxWidth * _cardWidthRatio;
            if (double.IsFinite(dimensions.MaxHeight)) card.MaxHeight = dimensions.MaxHeight * _cardHeightRatio;
            // List containers already occupy a proportional star-sized row.
            if (card.Child is ListBox || !card.IsEffectivelyVisible || card.Bounds.Height <= 0) continue;
            if (dimensions.BaseHeight == 0) dimensions.BaseHeight = card.Bounds.Height / _cardHeightRatio;
            var minimum = dimensions.BaseHeight * _cardHeightRatio;
            if (Math.Abs(card.MinHeight - minimum) > .5) card.MinHeight = minimum;
        }
    }
}
