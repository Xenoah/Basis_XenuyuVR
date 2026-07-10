using Basis.Scripts.BasisSdk;

namespace jp.lilxyzw.emock
{
    public interface IEmockAvatarLoad
    {
        // Called from EmockAnimator
        public void OnAvatarReady(BasisAvatar avatar, bool IsOwner);
    }
}
