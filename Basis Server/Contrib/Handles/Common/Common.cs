#nullable enable

using System.Collections;
using System.Collections.Generic;
using System.Threading.Tasks;
using Basis.Contrib.Auth.Handles.Newtypes;

namespace Basis.Contrib.Auth.Handles
{
	/// handle が指定 identity を指しているか解決する。
	public interface IHandleVerifier
	{
		/// この function の documentation は `HandleVerifier` を参照。
		public Task<bool> HandlePointsToIdentity(IHandle handle, Identity identity);

		/// handle の具体的な種類。
		public HandleKind Kind { get; }

		public HandleProperties Properties { get; }
	}

	/// すべての handle type は `IHandle` を実装する。
	public interface IHandle
	{
		/// handle の type。
		public HandleKind Kind { get; }

		public HandleProperties Properties { get; }

		/// 表示する display name を取得する。
		public string DisplayName { get; }
	}

	/// 特定の `HandleKind` kind/type に固有の情報。
	// TODO: record struct へ切り替える意味はあるか?
	public record HandleProperties(
		HandleKind Kind,
		HandleMutability Mutability,
		bool IsGloballyUnique
	);

	/// handle が指す identity set をどの程度変更できるか。
	public enum HandleMutability
	{
		/// handle は常に同じ identity set を指す。
		Immutable,

		/// identity が一度 set に追加されると残り続けるが、新しい identity も追加できる。
		AppendOnly,

		/// identity を set に自由に追加/削除できる。
		Mutable,
	}

	/// support する DidMethod の種類。
	public enum HandleKind
	{
		Local,
		Dns,
		// TODO: HttpWellKnown
		// TODO: Steam
	}
}
