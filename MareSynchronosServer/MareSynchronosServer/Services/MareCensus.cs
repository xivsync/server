using MareSynchronos.API.Dto.User;
using Prometheus;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;

namespace MareSynchronosServer.Services;

public class MareCensus : IHostedService
{
    private record CensusEntry(ushort WorldId, short Race, short Subrace, short Gender)
    {
        public static CensusEntry FromDto(CensusDataDto dto)
        {
            return new CensusEntry(dto.WorldId, dto.RaceId, dto.TribeId, dto.Gender);
        }
    }

    private readonly ConcurrentDictionary<string, CensusEntry> _censusEntries = new(StringComparer.Ordinal);
    private readonly Dictionary<short, string> _dcs = new()
    {
        {101, "陆行鸟"},
        {102, "莫古力"},
        {103, "猫小胖"},
        {201, "豆豆柴"},
    };
    private readonly Dictionary<short, string> _gender = new();
    private readonly ILogger<MareCensus> _logger;
    private readonly Dictionary<short, string> _races = new()
    {
        {1,"人族"},
        {2,"精灵族"},
        {3,"拉拉菲尔族"},
        {4,"猫魅族"},
        {5,"鲁加族"},
        {6,"敖龙族"},
        {7,"硌狮族"},
        {8,"维埃拉族"},
    };
    private readonly Dictionary<short, string> _tribes = new()
    {
        {1,"中原之民"},
        {2,"高地之民"},
        {3,"森林之民"},
        {4,"黑影之民"},
        {5,"平原之民"},
        {6,"沙漠之民"},
        {7,"逐日之民"},
        {8,"护月之民"},
        {9,"北洋之民"},
        {10,"红焰之民"},
        {11,"晨曦之民"},
        {12,"暮晖之民"},
        {13,"掠日之民"},
        {14,"迷踪之民"},
        {15,"密林之民"},
        {16,"山林之民"},
    };
    private readonly Dictionary<ushort, (string, short)> _worlds = new()
    {
        {1175, ("晨曦王座", 101) },
        {1174, ("沃仙曦染", 101) },
        {1173, ("宇宙和音", 101) },
        {1167, ("红玉海", 101)   },
        {1060, ("萌芽池", 101)   },
        {1081, ("神意之地", 101) },
        {1044, ("幻影群岛", 101) },
        {1042, ("拉诺西亚", 101) },

        {1121, ("拂晓之间", 102) },
        {1166, ("龙巢神殿", 102) },
        {1113, ("旅人栈桥", 102) },
        {1076, ("白金幻象", 102) },
        {1176, ("梦羽宝境", 102) },
        {1171, ("神拳痕", 102)   },
        {1170, ("潮风亭", 102)   },
        {1172, ("白银乡", 102)   },

        {1179, ("琥珀原", 103)   },
        {1178, ("柔风海湾", 103) },
        {1177, ("海猫茶屋", 103) },
        {1169, ("延夏", 103)    },
        {1106, ("静语庄园", 103) },
        {1045, ("摩杜纳", 103)   },
        {1043, ("紫水栈桥", 103) },

        {1201, ("红茶川", 201)    },
        {1186, ("伊修加德", 201)  },
        {1180, ("太阳海岸", 201)  },
        {1183, ("银泪湖", 201)    },
        {1192, ("水晶塔", 201)    },
        {1202, ("萨雷安", 201)    },
        {1203, ("加雷马", 201)    },
        {1200, ("亚马乌罗提", 201)},
    };
    private readonly string _xivApiKey;
    private Gauge? _gauge;

    public MareCensus(ILogger<MareCensus> logger, string xivApiKey)
    {
        _logger = logger;
        _xivApiKey = xivApiKey;
    }

    private bool Initialized => _gauge != null;

    public void ClearStatistics(string uid)
    {
        if (!Initialized) return;

        if (_censusEntries.Remove(uid, out var censusEntry))
        {
            ModifyGauge(censusEntry, increase: false);
        }
    }

    public void PublishStatistics(string uid, CensusDataDto? censusDataDto)
    {
        if (!Initialized || censusDataDto == null) return;

        var newEntry = CensusEntry.FromDto(censusDataDto);

        if (_censusEntries.TryGetValue(uid, out var entry))
        {
            if (entry != newEntry)
            {
                ModifyGauge(entry, increase: false);
                ModifyGauge(newEntry, increase: true);
                _censusEntries[uid] = newEntry;
            }
        }
        else
        {
            _censusEntries[uid] = newEntry;
            ModifyGauge(newEntry, increase: true);
        }
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        //if (string.IsNullOrEmpty(_xivApiKey)) return;

        _logger.LogInformation("Census:Init data");

        _gender[0] = "男";
        _gender[1] = "女";

        _gauge = Metrics.CreateGauge("mare_census", "mare informational census data", new[] { "dc", "world", "gender", "race", "subrace" });
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private void ModifyGauge(CensusEntry censusEntry, bool increase)
    {
        var subraceSuccess = _tribes.TryGetValue(censusEntry.Subrace, out var subrace);
        var raceSuccess = _races.TryGetValue(censusEntry.Race, out var race);
        var worldSuccess = _worlds.TryGetValue(censusEntry.WorldId, out var world);
        var genderSuccess = _gender.TryGetValue(censusEntry.Gender, out var gender);
        if (subraceSuccess && raceSuccess && worldSuccess && genderSuccess && _dcs.TryGetValue(world.Item2, out var dc))
        {
            if (increase)
                _gauge.WithLabels(dc, world.Item1, gender, race, subrace).Inc();
            else
                _gauge.WithLabels(dc, world.Item1, gender, race, subrace).Dec();
        }
    }
}
