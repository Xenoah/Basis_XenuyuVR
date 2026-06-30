using System;
using UnityEngine;

// decode 済み frame を OutputTexture として公開される Unity texture へ upload する。
// Renderer は生成する texture を所有し、frame dimension や pixel format が変わったときの
// 再構築責任を持つ。consumer は OnOutputTextureChanged を購読し、
// OutputTexture を material や UI element などへ bind する。
//
// scheduling logic に触れず YUV/RGBA path を差し替えられるよう、player から分離している。
public interface IBasisFrameRenderer : IDisposable
{
    bool SupportsFormat(BasisVideoFrameFormat format);

    // 現在の output texture。最初の frame が present されるまで、または
    // これまでの frame が全て SupportsFormat に拒否されている間は null。
    Texture OutputTexture { get; }

    // OutputTexture が再構築されるたびに発火する (初回作成、dimension 変更、format 変更)。
    // 新しい texture が引数で渡される。renderer が未準備状態へ遷移した場合は null の可能性がある。
    event Action<Texture> OnOutputTextureChanged;

    // frame の pixel data を OutputTexture へ upload する。成功時 true。
    // renderer 自身は OutputTexture を material へ bind しない。それは consumer
    // (BasisVideoMaterialOutput / BasisVideoDisplay) の責務。
    bool Present(BasisVideoFrame frame);
}
