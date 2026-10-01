using UdonSharp;

namespace UdonExpressionDriver
{
    /// <summary>
    /// Base for UED prop behaviours. Ignores puppet callbacks by default; subclasses
    /// override the ones they care about.
    /// </summary>
    public class UEDBehaviour : UEDPuppetHandler
    {
        public override void _OnPuppetRadial(float value)
        {
        }

        public override void _OnPuppetTwo(float x, float y)
        {
        }

        public override void _OnPuppetFour(float negX, float posX, float negY, float posY)
        {
        }

        public override void _OnPuppetClose()
        {
        }

        public override void _OnHandGesture(int left, int right)
        {
        }
    }
}
