using UdonSharp;

namespace UdonExpressionDriver
{
    /// <summary>
    /// Host of a RadialMenu: the menu forwards wedge presses here. Kept separate from
    /// UEDFullController so the menu can be driven without a full controller/prop.
    /// </summary>
    public abstract class UEDMenuHost : UEDBehaviour
    {
        /// <summary>Called when a wedge in the hosted RadialMenu is pressed.</summary>
        public abstract void _OnControlPressed(int controlIndex);
    }
}
