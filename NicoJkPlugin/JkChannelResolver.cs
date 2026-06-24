using System.Text.RegularExpressions;

namespace NicoJkPlugin;

internal sealed class JkChannelResolver
{
    private readonly PluginSettings _settings;
    private readonly Dictionary<string, int> _explicitTriplet = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _explicitName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Ch2Entry> _ch2Triplets = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ushort, int> _terrestrialServiceJk = new();

    public JkChannelResolver(PluginSettings settings, Action<string> log)
    {
        _settings = settings;
        foreach (var kv in settings.ChannelMapping)
        {
            if (TryTriplet(kv.Key, out var key)) _explicitTriplet[key] = kv.Value;
            else _explicitName[Norm(kv.Key)] = kv.Value;
        }
        if (settings.EnableTvTestChannelAutoMapping)
            LoadCh2(settings.Ch2Files);
        log($"[ChannelMap] 初期化: explicitTriplets={_explicitTriplet.Count} explicitNames={_explicitName.Count} serviceMaps={settings.ServiceChannelMapping.Count} ch2Entries={_ch2Triplets.Count} ch2TerrestrialSidMaps={_terrestrialServiceJk.Count} nationwideMaster=enabled");
    }

    public int? Resolve(ushort nid, ushort tsid, ushort sid, string serviceName)
    {
        var triplet = Key(nid, tsid, sid);
        if (_explicitTriplet.TryGetValue(triplet, out var v)) return v;

        var byServiceMap = ResolveByNicoJkServiceMap(nid, sid);
        if (byServiceMap is > 0) return byServiceMap;

        var name = Norm(serviceName);
        if (name.Length > 0 && _explicitName.TryGetValue(name, out v)) return v;

        // BS/CS は SID が取れている場合、地上波系列名より先に BS/CS の実況番号へ解決する。
        // 例: フジテレビONE/TWO/NEXT は「フジテレビ」という文字列だけで jk8 へ流さない。
        if (IsBsCs(nid) && BsCs.TryGetValue(sid, out v)) return v;

        var allowTerrestrialNameMap = MayUseTerrestrialNameMap(nid, sid, serviceName);

        // TvAIr / EPG から渡されたサービス名は、地上波またはID欠落時だけ全国局名一般マスタで補助する。
        // 全国の放送局名・圏域名は配布物に含めてよい一般マスタであり、個人環境値ではない。
        if (allowTerrestrialNameMap)
        {
            var byRuntimeName = InferByName(serviceName);
            if (byRuntimeName is > 0) return byRuntimeName;
        }

        if (_ch2Triplets.TryGetValue(triplet, out var ch2))
        {
            if (allowTerrestrialNameMap)
            {
                var byCh2Name = InferByName(ch2.Name);
                if (byCh2Name is > 0) return byCh2Name;
            }

            if (IsTerrestrial(nid) || nid == 0)
            {
                var byRemote = InferByRemoteKey(ch2.RemoteKey);
                if (byRemote is > 0) return byRemote;
            }
        }

        if (IsTerrestrial(nid) && _terrestrialServiceJk.TryGetValue(sid, out v)) return v;
        if (BsCs.TryGetValue(sid, out v) && IsBsCs(nid)) return v;
        return null;
    }

    private void LoadCh2(IEnumerable<string> files)
    {
        foreach (var file in files)
        {
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#')) continue;
                var cols = line.Split(',', StringSplitOptions.None).Select(x => x.Trim()).ToArray();
                if (TryParseCh2(cols, out var name, out var nid, out var tsid, out var sid, out var remote))
                {
                    _ch2Triplets[Key(nid, tsid, sid)] = new(name, remote);

                    var jkByName = InferByName(name);
                    if (jkByName is > 0 && IsTerrestrial(nid))
                    {
                        _terrestrialServiceJk[sid] = jkByName.Value;
                        continue;
                    }

                    var jkByRemote = InferByRemoteKey(remote);
                    if (jkByRemote is > 0 && IsTerrestrial(nid))
                        _terrestrialServiceJk[sid] = jkByRemote.Value;
                }
            }
        }
    }

    private static bool TryParseCh2(string[] cols, out string name, out ushort nid, out ushort tsid, out ushort sid, out int? remote)
    {
        name = cols.Length > 0 ? cols[0] : string.Empty;
        nid = tsid = sid = 0;
        remote = null;
        if (cols.Length < 4 || string.IsNullOrWhiteSpace(name)) return false;

        // TVTest .ch2 は環境・BonDriverにより列数差が出るため、代表的な並びを順に試す。
        // 旧来想定: name,...,serviceId,networkId,transportStreamId
        if (TryTripletColumns(cols, 9, 10, 8, out nid, out tsid, out sid))
        {
            remote = TryRemote(cols);
            return true;
        }

        // よくある並び: name,tuningSpace,channel,remoteKey,serviceId,networkId,transportStreamId,...
        if (TryTripletColumns(cols, 5, 6, 4, out nid, out tsid, out sid))
        {
            remote = TryRemote(cols);
            return true;
        }

        return TryParseCh2Heuristic(cols, out name, out nid, out tsid, out sid, out remote);
    }

    private static bool TryParseCh2Heuristic(string[] cols, out string name, out ushort nid, out ushort tsid, out ushort sid, out int? remote)
    {
        name = cols.Length > 0 ? cols[0] : string.Empty;
        nid = tsid = sid = 0;
        remote = null;
        if (cols.Length < 4 || string.IsNullOrWhiteSpace(name)) return false;

        var nums = new List<(int Index, int Value)>();
        for (var i = 0; i < cols.Length; i++)
        {
            if (int.TryParse(cols[i], out var v)) nums.Add((i, v));
        }
        if (nums.Count == 0) return false;

        var terrestrialNetwork = nums
            .Where(x => x.Value is >= 30000 and <= 33000)
            .GroupBy(x => x.Value)
            .Where(g => g.Count() >= 2)
            .Select(g => g.Key)
            .FirstOrDefault();
        var sidCandidate = nums.FirstOrDefault(x => x.Value is >= 40000 and <= 60000);
        if (terrestrialNetwork > 0 && sidCandidate.Value > 0)
        {
            nid = (ushort)terrestrialNetwork;
            tsid = (ushort)terrestrialNetwork;
            sid = (ushort)sidCandidate.Value;
            remote = TryRemoteNearServiceId(cols, sidCandidate.Index) ?? TryRemote(cols);
            return true;
        }

        return false;
    }

    private static bool TryTripletColumns(string[] cols, int nidIndex, int tsidIndex, int sidIndex, out ushort nid, out ushort tsid, out ushort sid)
    {
        nid = tsid = sid = 0;
        if (cols.Length <= Math.Max(nidIndex, Math.Max(tsidIndex, sidIndex))) return false;
        return ushort.TryParse(cols[nidIndex], out nid)
            && ushort.TryParse(cols[tsidIndex], out tsid)
            && ushort.TryParse(cols[sidIndex], out sid)
            && nid > 0 && tsid > 0 && sid > 0;
    }

    private static int? TryRemote(string[] cols)
    {
        foreach (var idx in new[] { 3, 13, 2 })
        {
            if (cols.Length > idx && int.TryParse(cols[idx], out var r) && r is >= 1 and <= 12) return r;
        }
        return null;
    }

    private static int? TryRemoteNearServiceId(string[] cols, int sidIndex)
    {
        for (var i = sidIndex - 1; i >= 0; i--)
        {
            if (int.TryParse(cols[i], out var r) && r is >= 1 and <= 12) return r;
        }
        for (var i = sidIndex + 1; i < cols.Length; i++)
        {
            if (int.TryParse(cols[i], out var r) && r is >= 1 and <= 12) return r;
        }
        return null;
    }

    private static int? InferByRemoteKey(int? key)
        => key switch { 1 => 1, 2 => 2, 4 => 4, 5 => 5, 6 => 6, 7 => 7, 8 => 8, _ => null };

    private int? ResolveByNicoJkServiceMap(ushort nid, ushort sid)
    {
        var terrestrial = _settings.ServiceChannelMapping.TryGetValue(PluginSettings.ServiceKey(15, sid), out var jk) ? jk : 0;
        if (terrestrial > 0) return terrestrial;
        var network = _settings.ServiceChannelMapping.TryGetValue(PluginSettings.ServiceKey(nid, sid), out jk) ? jk : 0;
        return network > 0 ? jk : null;
    }

    private static int? InferByName(string? raw)
    {
        var n = Norm(raw);
        if (n.Length == 0) return null;

        foreach (var group in NationwideStationNameMap)
        {
            foreach (var word in group.Names)
            {
                if (n.Contains(Norm(word), StringComparison.OrdinalIgnoreCase))
                    return group.Jk;
            }
        }

        return null;
    }

    private static string Norm(string? value) => string.IsNullOrWhiteSpace(value) ? string.Empty : Regex.Replace(value.Normalize(), "[\\s　]+", "");

    private static bool MayUseTerrestrialNameMap(ushort nid, ushort sid, string? serviceName)
    {
        if (IsTerrestrial(nid)) return true;
        if (IsBsCs(nid)) return false;

        // ID欠落時の救済は残す。ただし、明らかなBS/CS局名を地上波系列へ誤写像しない。
        // この判定はチャンネルマッピング内だけに閉じ、セッション監視・コメント配信の出口には触れない。
        if (nid == 0 && sid == 0)
        {
            var n = Norm(serviceName);
            if (n.Length == 0) return false;
            foreach (var word in NonTerrestrialNameHints)
            {
                if (n.Contains(Norm(word), StringComparison.OrdinalIgnoreCase)) return false;
            }
            return true;
        }

        return false;
    }
    private static bool IsTerrestrial(ushort nid) => nid is >= 30000 and <= 33000;
    private static bool IsBsCs(ushort nid) => nid is >= 4 and <= 10;
    private static string Key(ushort nid, ushort tsid, ushort sid) => $"{nid}:{tsid}:{sid}";
    private static bool TryTriplet(string raw, out string key)
    {
        key = string.Empty;
        var p = raw.Split(':', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (p.Length != 3) return false;
        if (!ushort.TryParse(p[0], out var nid) || !ushort.TryParse(p[1], out var tsid) || !ushort.TryParse(p[2], out var sid)) return false;
        key = Key(nid, tsid, sid); return true;
    }

    private readonly record struct Ch2Entry(string Name, int? RemoteKey);
    private readonly record struct StationNameGroup(int Jk, string[] Names);

    private static readonly StationNameGroup[] NationwideStationNameMap =
    {
        new(1, new[]
        {
            "NHK総合", "ＮＨＫ総合", "NHKG", "ＮＨＫＧ", "総合テレビ"
        }),
        new(2, new[]
        {
            "Eテレ", "Ｅテレ", "NHKEテレ", "ＮＨＫＥテレ", "NHK教育", "ＮＨＫ教育", "教育テレビ"
        }),
        new(4, new[]
        {
            "日本テレビ", "日テレ", "読売テレビ", "札幌テレビ", "STV", "ＳＴＶ", "青森放送", "RAB", "ＲＡＢ",
            "テレビ岩手", "ミヤギテレビ", "宮城テレビ", "秋田放送", "ABS", "ＡＢＳ", "山形放送", "YBC", "ＹＢＣ",
            "福島中央テレビ", "FCT", "ＦＣＴ", "テレビ新潟", "TeNY", "ＴｅＮＹ", "山梨放送", "YBS", "ＹＢＳ",
            "テレビ信州", "TSB", "ＴＳＢ", "静岡第一テレビ", "SDT", "ＳＤＴ", "北日本放送", "KNB", "ＫＮＢ",
            "テレビ金沢", "KTK", "ＫＴＫ", "福井放送", "FBC", "ＦＢＣ", "中京テレビ", "CTV", "ＣＴＶ",
            "日本海テレビ", "NKT", "ＮＫＴ", "広島テレビ", "HTV", "ＨＴＶ", "山口放送", "KRY", "ＫＲＹ",
            "四国放送", "JRT", "ＪＲＴ", "西日本放送", "RNC", "ＲＮＣ", "南海放送", "RNB", "ＲＮＢ",
            "高知放送", "RKC", "ＲＫＣ", "福岡放送", "FBS", "ＦＢＳ", "長崎国際テレビ", "NIB", "ＮＩＢ",
            "熊本県民テレビ", "KKT", "ＫＫＴ", "鹿児島読売テレビ", "KYT", "ＫＹＴ"
        }),
        new(5, new[]
        {
            "テレビ朝日", "テレ朝", "朝日放送", "ABCテレビ", "ＡＢＣテレビ", "北海道テレビ", "HTB", "ＨＴＢ",
            "青森朝日放送", "ABA", "ＡＢＡ", "岩手朝日テレビ", "IAT", "ＩＡＴ", "東日本放送", "KHB", "ＫＨＢ",
            "秋田朝日放送", "AAB", "ＡＡＢ", "山形テレビ", "YTS", "ＹＴＳ", "福島放送", "KFB", "ＫＦＢ",
            "新潟テレビ21", "新潟テレビ２１", "UX", "ＵＸ", "長野朝日放送", "ABN", "ＡＢＮ", "静岡朝日テレビ", "SATV", "ＳＡＴＶ",
            "北陸朝日放送", "HAB", "ＨＡＢ", "メ～テレ", "名古屋テレビ", "NBN", "ＮＢＮ", "瀬戸内海放送", "KSB", "ＫＳＢ",
            "広島ホームテレビ", "広島ホーム", "HOME", "ＨＯＭＥ", "山口朝日放送", "YAB", "ＹＡＢ", "愛媛朝日テレビ", "EAT", "ＥＡＴ",
            "九州朝日放送", "KBC", "ＫＢＣ", "長崎文化放送", "NCC", "ＮＣＣ", "熊本朝日放送", "KAB", "ＫＡＢ",
            "大分朝日放送", "OAB", "ＯＡＢ", "鹿児島放送", "KKB", "ＫＫＢ", "琉球朝日放送", "QAB", "ＱＡＢ"
        }),
        new(6, new[]
        {
            "TBS", "ＴＢＳ", "北海道放送", "HBC", "ＨＢＣ", "青森テレビ", "ATV", "ＡＴＶ", "IBC岩手放送", "IBC", "ＩＢＣ",
            "東北放送", "TBC", "ＴＢＣ", "テレビユー山形", "TUY", "ＴＵＹ", "テレビユー福島", "TUF", "ＴＵＦ",
            "新潟放送", "BSN", "ＢＳＮ", "信越放送", "SBC", "ＳＢＣ", "テレビ山梨", "UTY", "ＵＴＹ",
            "静岡放送", "SBS", "ＳＢＳ", "北陸放送", "MRO", "ＭＲＯ", "CBC", "ＣＢＣ", "毎日放送", "MBS", "ＭＢＳ",
            "山陰放送", "BSS", "ＢＳＳ", "RSK山陽放送", "山陽放送", "RSK", "ＲＳＫ", "中国放送", "RCC", "ＲＣＣ",
            "テレビ山口", "TYS", "ＴＹＳ", "あいテレビ", "ITV", "ＩＴＶ", "テレビ高知", "KUTV", "ＫＵＴＶ",
            "RKB毎日放送", "RKB", "ＲＫＢ", "長崎放送", "NBC", "ＮＢＣ", "熊本放送", "RKK", "ＲＫＫ",
            "大分放送", "OBS", "ＯＢＳ", "宮崎放送", "MRT", "ＭＲＴ", "南日本放送", "MBC", "ＭＢＣ", "琉球放送", "RBC", "ＲＢＣ"
        }),
        new(7, new[]
        {
            "テレビ東京", "テレ東", "テレビ北海道", "TVh", "ＴＶｈ", "テレビ愛知", "TVA", "ＴＶＡ",
            "テレビ大阪", "TVO", "ＴＶＯ", "テレビせとうち", "TSC", "ＴＳＣ", "TVQ九州放送", "TVQ", "ＴＶＱ"
        }),
        new(8, new[]
        {
            "フジテレビ", "北海道文化放送", "UHB", "ＵＨＢ", "岩手めんこいテレビ", "MIT", "ＭＩＴ", "仙台放送", "OX", "ＯＸ",
            "秋田テレビ", "AKT", "ＡＫＴ", "さくらんぼテレビ", "SAY", "ＳＡＹ", "福島テレビ", "FTV", "ＦＴＶ",
            "NST", "ＮＳＴ", "新潟総合テレビ", "長野放送", "NBS", "ＮＢＳ", "テレビ静岡", "SUT", "ＳＵＴ",
            "富山テレビ", "BBT", "ＢＢＴ", "石川テレビ", "ITC", "ＩＴＣ", "福井テレビ", "FTB", "ＦＴＢ",
            "東海テレビ", "THK", "ＴＨＫ", "関西テレビ", "KTV", "ＫＴＶ", "山陰中央テレビ", "TSK", "ＴＳＫ",
            "岡山放送", "OHK", "ＯＨＫ", "テレビ新広島", "TSS", "ＴＳＳ", "テレビ愛媛", "EBC", "ＥＢＣ",
            "高知さんさんテレビ", "KSS", "ＫＳＳ", "テレビ西日本", "TNC", "ＴＮＣ", "サガテレビ", "STS", "ＳＴＳ",
            "テレビ長崎", "KTN", "ＫＴＮ", "テレビ熊本", "TKU", "ＴＫＵ", "テレビ大分", "TOS", "ＴＯＳ",
            "テレビ宮崎", "UMK", "ＵＭＫ", "鹿児島テレビ", "KTS", "ＫＴＳ", "沖縄テレビ", "OTV", "ＯＴＶ"
        }),
        new(9, new[]
        {
            "TOKYOMX", "ＴＯＫＹＯＭＸ", "TOKYO MX", "ＴＯＫＹＯ MX", "MXテレビ", "ＭＸテレビ", "東京MX", "東京ＭＸ"
        })
    };

    private static readonly string[] NonTerrestrialNameHints =
    {
        "BS", "ＢＳ", "CS", "ＣＳ", "WOWOW", "ＷＯＷＯＷ", "スターチャンネル",
        "フジテレビONE", "フジテレビTWO", "フジテレビNEXT", "フジテレビＯＮＥ", "フジテレビＴＷＯ", "フジテレビＮＥＸＴ",
        "J SPORTS", "Ｊ ＳＰＯＲＴＳ", "JSPORTS", "ＪＳＰＯＲＴＳ", "スカチャン", "スカパー",
        "アニマックス", "キッズステーション", "AT-X", "ＡＴ－Ｘ", "チャンネルNECO", "チャンネルＮＥＣＯ", "映画・ｃｈＮＥＣＯ",
        "日テレジータス", "日テレプラス", "テレ朝チャンネル", "TBSチャンネル", "ＴＢＳチャンネル",
        "時代劇専門チャンネル", "衛星劇場", "東映チャンネル", "ファミリー劇場", "ホームドラマチャンネル",
        "GAORA", "ＧＡＯＲＡ", "スカイA", "スカイＡ", "グリーンチャンネル", "ディズニー", "ムービープラス"
    };

    private static readonly Dictionary<ushort, int> BsCs = new()
    {
        [101]=101,[102]=101,[103]=103,[141]=141,[151]=151,[161]=161,[171]=171,[181]=181,
        [191]=191,[192]=192,[193]=193,[200]=200,[201]=201,[211]=211,[222]=222,[236]=236,
        [260]=260,[333]=333,[55]=55,[218]=218,[219]=219,[223]=223,[227]=227,[229]=229,
        [234]=234,[240]=240,[241]=241,[242]=242,[243]=243,[244]=244,[245]=245,[250]=250,
        [251]=251,[252]=252,[254]=254,[255]=255,[256]=256,[257]=257,[262]=262,[290]=290,
        [292]=292,[293]=293,[294]=294,[295]=295,[296]=296,[297]=297,[298]=298,[299]=299,
        [300]=300,[301]=301,[305]=305,[307]=307,[308]=308,[309]=309,[310]=310,[311]=311,
        [312]=312,[317]=317,[318]=318,[320]=320,[321]=321,[322]=322,[323]=323,[324]=324,
        [325]=325,[329]=329,[330]=330,[331]=331,[339]=339,[340]=340,[341]=341,
        [342]=342,[343]=343,[349]=349,[351]=351,[353]=353,[800]=800,[801]=801
    };
}
