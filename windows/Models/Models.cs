using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Lexi.Core;

namespace Lexi;

public sealed class WordItem : INotifyPropertyChanged
{
    private bool _selected;
    private string _translation = "";
    private string _phonetic = "";
    private string _definition = "";
    private int _stage;
    private string _status = "learning";
    private string? _nextReviewDate;
    private string? _lastReviewedAt;
    private int _reviewCount;
    private ArchiveMetadata _archive = new();
    private string _notes = "";

    public long Id { get; set; }
    public string Word { get; set; } = "";

    public ArchiveMetadata Archive
    {
        get => _archive;
        set { if (_archive != value) { _archive = value ?? throw new ArgumentNullException(nameof(value)); OnPropertyChanged(); } }
    }

    public string Phonetic
    {
        get => _phonetic;
        set { if (_phonetic != value) { _phonetic = value; OnPropertyChanged(); } }
    }

    public string Translation
    {
        get => _translation;
        set { if (_translation != value) { _translation = value; OnPropertyChanged(); } }
    }

    public string Definition
    {
        get => _definition;
        set { if (_definition != value) { _definition = value; OnPropertyChanged(); } }
    }

    public string Notes { get => _notes; set { if (_notes != value) { _notes = value; OnPropertyChanged(); } } }

    public int Stage
    {
        get => _stage;
        set
        {
            if (_stage != value)
            {
                _stage = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(StageDescription));
            }
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusLabel));
                OnPropertyChanged(nameof(IsMastered));
            }
        }
    }

    public string CreatedAt { get; set; } = "";
    public string LearningStartDate { get; set; } = "";

    public string? NextReviewDate
    {
        get => _nextReviewDate;
        set
        {
            if (_nextReviewDate != value)
            {
                _nextReviewDate = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(NextReviewDisplay));
            }
        }
    }

    public string? LastReviewedAt
    {
        get => _lastReviewedAt;
        set { if (_lastReviewedAt != value) { _lastReviewedAt = value; OnPropertyChanged(); } }
    }

    public int ReviewCount
    {
        get => _reviewCount;
        set { if (_reviewCount != value) { _reviewCount = value; OnPropertyChanged(); } }
    }

    public bool Selected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; OnPropertyChanged(); } }
    }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { if (_isExpanded != value) { _isExpanded = value; OnPropertyChanged(); } }
    }

    public bool IsMastered => Status == "mastered";

    public bool UsesAdaptiveSchedule { get; set; }
    public string StatusLabel => Status == "mastered" ? "已掌握" : UsesAdaptiveSchedule ? "FSRS 自适应" : $"阶段 {Stage + 1} / 5";

    public string StageDescription => Status == "mastered" ? "全部完成" : UsesAdaptiveSchedule ? "按记忆表现动态排期" : $"第 {Stage + 1} 阶段 (共 5 阶段)";

    public string NextReviewDisplay => Status == "mastered" ? "完成学习" : (NextReviewDate ?? "待安排");

    private bool _optExamples = true;
    public bool OptExamples
    {
        get => _optExamples;
        set { if (_optExamples != value) { _optExamples = value; OnPropertyChanged(); } }
    }

    private bool _optSynonyms = true;
    public bool OptSynonyms
    {
        get => _optSynonyms;
        set { if (_optSynonyms != value) { _optSynonyms = value; OnPropertyChanged(); } }
    }

    private bool _optAntonyms;
    public bool OptAntonyms
    {
        get => _optAntonyms;
        set { if (_optAntonyms != value) { _optAntonyms = value; OnPropertyChanged(); } }
    }

    private bool _optPhrases;
    public bool OptPhrases
    {
        get => _optPhrases;
        set { if (_optPhrases != value) { _optPhrases = value; OnPropertyChanged(); } }
    }

    private bool _isGeneratingAi;
    public bool IsGeneratingAi
    {
        get => _isGeneratingAi;
        set
        {
            if (_isGeneratingAi != value)
            {
                _isGeneratingAi = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AiButtonText));
            }
        }
    }

    public string AiButtonText => IsGeneratingAi ? "生成中…" : "✦ 生成";

    private string _aiStatusText = "";
    public string AiStatusText
    {
        get => _aiStatusText;
        set { if (_aiStatusText != value) { _aiStatusText = value; OnPropertyChanged(); } }
    }

    private LlmResult? _aiResult;
    public LlmResult? AiResult
    {
        get => _aiResult;
        set
        {
            if (_aiResult != value)
            {
                _aiResult = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasAiResult));
                OnPropertyChanged(nameof(HasAiExamples));
                OnPropertyChanged(nameof(HasAiSynonyms));
                OnPropertyChanged(nameof(HasAiAntonyms));
                OnPropertyChanged(nameof(HasAiPhrases));
                OnPropertyChanged(nameof(AiExamples));
                OnPropertyChanged(nameof(AiSynonyms));
                OnPropertyChanged(nameof(AiAntonyms));
                OnPropertyChanged(nameof(AiPhrases));
            }
        }
    }

    public bool HasAiResult => _aiResult != null && _aiResult.HasContent;
    public bool HasAiExamples => _aiResult != null && _aiResult.Examples.Count > 0;
    public bool HasAiSynonyms => _aiResult != null && _aiResult.Synonyms.Count > 0;
    public bool HasAiAntonyms => _aiResult != null && _aiResult.Antonyms.Count > 0;
    public bool HasAiPhrases => _aiResult != null && _aiResult.Phrases.Count > 0;

    public List<ExampleItem> AiExamples => _aiResult?.Examples ?? new();
    public List<string> AiSynonyms => _aiResult?.Synonyms ?? new();
    public List<string> AiAntonyms => _aiResult?.Antonyms ?? new();
    public List<PhraseItem> AiPhrases => _aiResult?.Phrases ?? new();

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}


