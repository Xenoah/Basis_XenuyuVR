# User Handles (username / display name)

Basis で handle / username を表現するための、拡張可能な system です。

user は peer や server に handle を報告します。1 つ以上の handle を公開でき、それぞれ異なる source を持ちます。

* *Local*: user が自分の handle を選び、潜在的な衝突は server と調整します。一意性の保証は instance ごとに限られ、"先着順" です。security の保証は最も低く、handle の custody は証明できません。ただし、local handle は他の service / account を必要としない利点があります。
* *DNS*: player の DID へ link する DNS TXT record によって custody を証明します。bluesky の [ATProto][atproto handle] と同一、またはほぼ同一の方式です。global に一意であることと、なりすましへの安全性が保証されます。
* *HTTPS Well-Known*: player の DID へ link する (sub)domain 配下の `.well-known` endpoint への GET request によって custody を証明します。bluesky の [ATProto][atproto handle] と同一、またはほぼ同一の方式です。global に一意であることと、なりすましへの安全性が保証されます。
* *Steam (TODO)*: steam id によって custody を証明します。
* *Oculus (TODO)*: meta account によって custody を証明します。

これらの API は、handle 周辺の UX や、どの handle system を選ぶかに関する判断を意図的に管理しません。その判断は application developer に委ね、最大限の柔軟性を確保しつつ、意見を最小限にするためです。

[atproto handle]: https://atproto.com/specs/handle
