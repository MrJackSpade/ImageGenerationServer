# Frozen validation cases for the Ideogram 4 correction.
#
# This file is DATA, not executable setup logic. It is separate from
# reproduction.config.psd1 so that:
#
#   1. People can run arbitrary prompts without editing the main script.
#   2. The historical validation panel remains visible and reproducible.
#   3. Changes to the benchmark are easy to review in a normal text diff.
#
# Normal use:
#   Edit reproduction.config.psd1 and leave this file untouched.
#
# Benchmark use:
#   Run the script with -Mode Smoke or -Mode FullValidation. Those modes load
#   this manifest explicitly and ignore custom prompt settings.
#
# HistoricalBaseline records the observed unmodified result for that exact
# prompt/seed pair. It is reference metadata; the script does not use it to
# classify newly generated images automatically.
@{
    Schema = 'ideogram4_frozen_validation_cases_v2'

    # Smoke mode selects this one case from the full panel.
    SmokeCaseId = 'validation-01'

    # The confirmatory panel contains eight historical artifact cases and
    # eight same-prompt reference-clean cases with fixed seeds.
    Cases = @(
        @{
            Id = 'validation-01'
            Prompt = 'a black bear crossing a rocky river below a pine forest'
            Seed = 587896340
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-02'
            Prompt = 'a narrow waterfall between moss-covered cliffs after steady rain'
            Seed = 1058028457
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-03'
            Prompt = 'a narrow waterfall between moss-covered cliffs after steady rain'
            Seed = 267695714
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-04'
            Prompt = 'a red bicycle leaning against a pale brick wall in afternoon shade'
            Seed = 1058028457
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-05'
            Prompt = 'a black bear crossing a rocky river below a pine forest'
            Seed = 1058028457
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-06'
            Prompt = 'a red bicycle leaning against a pale brick wall in afternoon shade'
            Seed = 267695714
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-07'
            Prompt = 'a black bear crossing a rocky river below a pine forest'
            Seed = 704391158
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-08'
            Prompt = 'a stone footbridge over a shallow woodland creek in autumn'
            Seed = 267695714
            HistoricalBaseline = 'artifact'
        }
        @{
            Id = 'validation-09'
            Prompt = 'a black bear crossing a rocky river below a pine forest'
            Seed = 1715130591
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-10'
            Prompt = 'a black bear crossing a rocky river below a pine forest'
            Seed = 843102230
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-11'
            Prompt = 'a red bicycle leaning against a pale brick wall in afternoon shade'
            Seed = 85997382
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-12'
            Prompt = 'a red bicycle leaning against a pale brick wall in afternoon shade'
            Seed = 1193949114
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-13'
            Prompt = 'a stone footbridge over a shallow woodland creek in autumn'
            Seed = 125483577
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-14'
            Prompt = 'a stone footbridge over a shallow woodland creek in autumn'
            Seed = 1027772268
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-15'
            Prompt = 'a narrow waterfall between moss-covered cliffs after steady rain'
            Seed = 1765669222
            HistoricalBaseline = 'clean'
        }
        @{
            Id = 'validation-16'
            Prompt = 'a narrow waterfall between moss-covered cliffs after steady rain'
            Seed = 470420131
            HistoricalBaseline = 'clean'
        }
    )
}
