using Microsoft.CodeAnalysis;
using XUnit_IngeniousGenerator.Core;

namespace XUnit_IngeniousGenerator;

// 警告のタイトル・メッセージと Fix のタイトル。
// 文言は Core の Messages.resx（日本語は Messages.ja.resx）にまとめる。アナライザー本体の日本語のリソースは、
// 会社の Visual Studio で読み込まれず英語になったため（Core の日本語のリソースは読み込まれていた）
internal static class Resources
{
    /// <summary>
    /// 表示言語（UiLanguage）の文言を取り出し、引数を埋め込む
    /// </summary>
    /// <param name="name">文言の名前</param>
    /// <param name="arguments">埋め込む値</param>
    /// <returns>文言</returns>
    public static string Get(string name, params object[] arguments)
    {
        return Messages.Get(name, arguments);
    }

    /// <summary>
    /// 警告のタイトルなど、表示するときに文言を取り出す文字列を作る。
    /// IDE が渡す言語は使わず、UiLanguage で決めた言語にする（VS では、IDE が渡す言語が英語になっていたため）
    /// </summary>
    /// <param name="name">文言の名前</param>
    /// <returns>文字列</returns>
    public static LocalizableString Localizable(string name)
    {
        return new UiLanguageString(name);
    }

    /// <summary>
    /// 表示するときに、UiLanguage で決めた言語の文言を取り出す文字列
    /// </summary>
    private sealed class UiLanguageString : LocalizableString
    {
        private readonly string _name;

        /// <summary>
        /// 文言の名前を覚えておく
        /// </summary>
        /// <param name="name">文言の名前</param>
        public UiLanguageString(string name)
        {
            _name = name;
        }

        /// <summary>
        /// 文言を返す（IDE が渡す言語は使わない）
        /// </summary>
        /// <param name="formatProvider">IDE が渡す言語</param>
        /// <returns>文言</returns>
        protected override string GetText(IFormatProvider? formatProvider)
        {
            return Messages.Get(_name);
        }

        /// <summary>
        /// 同じ文言かを判定する
        /// </summary>
        /// <param name="other">比べる文字列</param>
        /// <returns>同じ名前ならtrue</returns>
        protected override bool AreEqual(object? other)
        {
            return other is UiLanguageString text && text._name == _name;
        }

        /// <summary>
        /// ハッシュ値を返す
        /// </summary>
        /// <returns>ハッシュ値</returns>
        protected override int GetHash()
        {
            return StringComparer.Ordinal.GetHashCode(_name);
        }
    }
}
