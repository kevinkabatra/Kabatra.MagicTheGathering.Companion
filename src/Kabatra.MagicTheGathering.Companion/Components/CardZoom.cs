using Kabatra.MagicTheGathering.Companion.Core.Cards;
using Microsoft.AspNetCore.Components.Web;

namespace Kabatra.MagicTheGathering.Companion.Components;

/// <summary>
/// Which card thumbnail to show large: the one hovered with a mouse, or the one held on a touch screen. Wire a
/// thumbnail's pointer events to these methods, run its click through <see cref="TakeClick"/>, and render
/// <c>&lt;ZoomedCard Card="zoom.Card" /&gt;</c>. Methods that change what's shown are event handlers, so the
/// component re-renders after them.
/// </summary>
public sealed class CardZoom : IDisposable
{
    private const int LongPressMs = 450;

    private CancellationTokenSource? _press;

    /// <summary>Set after a long press, so lifting the finger doesn't also click the thumbnail.</summary>
    private bool _swallowClick;

    /// <summary>The card to show large, if any.</summary>
    public CardInfo? Card { get; private set; }

    public void PointerEnter(PointerEventArgs e, CardInfo card)
    {
        if (e.PointerType == "mouse") Card = card;
    }

    public async Task PointerDown(PointerEventArgs e, CardInfo card)
    {
        if (e.PointerType == "mouse") return;
        _press?.Cancel();
        _press = new CancellationTokenSource();
        var token = _press.Token;
        _swallowClick = false;
        try
        {
            await Task.Delay(LongPressMs, token);
            Card = card;
            _swallowClick = true;
        }
        catch (TaskCanceledException) { } // lifted, or scrolled away, before it counted as a long press
    }

    /// <summary>Finger lifted: a short tap goes on to click; a long press just closes the zoom.</summary>
    public void PointerUp()
    {
        _press?.Cancel();
        if (_swallowClick) Card = null;
    }

    /// <summary>Pointer left the thumbnail, or the press was cancelled (e.g. by scrolling).</summary>
    public void End(CardInfo card)
    {
        _press?.Cancel();
        if (Card?.Id == card.Id) Card = null;
    }

    /// <summary>Whether a click should go ahead: false for the click that ends a long press.</summary>
    public bool TakeClick()
    {
        if (_swallowClick)
        {
            _swallowClick = false;
            return false;
        }
        Card = null;
        return true;
    }

    public void Dispose() => _press?.Cancel();
}
