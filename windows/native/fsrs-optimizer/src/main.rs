use std::io::{self, Read, Write};
use std::process;
use fsrs::{ComputeParametersInput, FSRSItem, FSRSReview, FSRS, DEFAULT_PARAMETERS};
use serde::{Deserialize, Serialize};

pub const HELPER_VERSION: &str = "0.2.0";
pub const PROTOCOL_VERSION: u32 = 1;
pub const PROTOCOL_NAME: &str = "fsrs-optimizer-v1";
pub const ALGORITHM_NAME: &str = "FSRS-6";
pub const UPSTREAM_VERSION: &str = "5.2.0";
pub const UPSTREAM_GIT_SHA: &str = "aca2838bfbdc6f15ca3f7a0c96a99fae466c9e9c";
pub const UPSTREAM_CRATE_CHECKSUM: &str = "cab5f80c16d1d07e492a6828478ecb9f379f142ba6f19fa3b7955edd180ddc2e";
pub const UPSTREAM_LICENSE: &str = "BSD-3-Clause";
pub const COMPATIBILITY_ID: &str = "fsrs6-hard-floor-2-v1";
pub const PATCH_SHA256: &str = "4c52a7583e4c939a93641b1655872acc026186a9b5a5524afd7b83a5d1eed5bb";

/// Budgets and resource constraints
pub const MAX_INPUT_BYTES: usize = 8 * 1024 * 1024; // 8 MB
pub const MAX_ITEMS_BUDGET: usize = 50_000;
pub const MAX_TOTAL_REVIEW_CELLS: usize = 500_000;
pub const MAX_SEQUENCE_LENGTH: usize = 64;
pub const OFFICIAL_FIXED_SEED: u64 = 2023;

/// Official clip bounds for FSRS-6 21 parameters
pub const OFFICIAL_CLIP_BOUNDS: [(f32, f32); 21] = [
    (0.001, 100.0), (0.001, 100.0), (0.001, 100.0), (0.001, 100.0), // w0..w3  S0
    (1.0, 10.0),                                                    // w4      D0 constant
    (0.001, 4.0), (0.001, 4.0), (0.001, 0.75),                      // w5..w7
    (0.0, 4.5), (0.0, 0.8), (0.001, 3.5),                           // w8..w10 S success
    (0.001, 5.0), (0.001, 0.25), (0.001, 0.9), (0.0, 4.0),          // w11..w14 S fail
    (0.0, 1.0), (1.0, 6.0),                                         // w15 hard penalty, w16 easy bonus
    (0.0, 2.0), (0.0, 2.0), (0.0, 0.8),                             // w17..w19 short term
    (0.1, 0.8),                                                     // w20     decay
];

#[derive(Debug, Deserialize)]
pub struct InputReview {
    pub rating: u32,
    pub delta_t: u32,
}

#[derive(Debug, Deserialize)]
pub struct InputItem {
    pub reviews: Vec<InputReview>,
}

#[derive(Debug, Deserialize)]
pub struct OptimizeRequest {
    pub version: u32,
    pub algorithm: String,
    #[serde(default = "default_enable_short_term")]
    pub enable_short_term: bool,
    #[serde(default = "default_threads")]
    pub threads: usize,
    #[serde(default = "default_seed")]
    pub seed: u64,
    #[serde(default)]
    pub filter_outliers: Option<bool>,
    pub items: Vec<InputItem>,
}

fn default_enable_short_term() -> bool {
    true
}

fn default_threads() -> usize {
    1
}

fn default_seed() -> u64 {
    OFFICIAL_FIXED_SEED
}

#[derive(Debug, Serialize)]
pub struct OptimizeResponse {
    pub version: u32,
    pub algorithm: String,
    pub protocol: String,
    pub model_version: String,
    pub git_sha: String,
    pub parameter_count: usize,
    pub weights: Vec<f32>,
    pub effective_seed: u64,
    pub items_count: usize,
    pub evolved_count: usize,
    pub max_sequence_length: usize,
    pub compatibility_id: String,
    pub compatibility_revision: String,
    pub patch_sha256: String,
}

#[derive(Debug, Serialize)]
pub struct ProbeResponse {
    pub helper_version: String,
    pub protocol_version: u32,
    pub protocol: String,
    pub algorithm: String,
    pub upstream_version: String,
    pub upstream_git_sha: String,
    pub upstream_crate_checksum: String,
    pub upstream_license: String,
    pub compatibility_id: String,
    pub compatibility_revision: String,
    pub patch_sha256: String,
    pub s_min: f32,
    pub default_decay: f32,
    pub max_sequence_length: usize,
    pub fixed_seed: u64,
    pub default_weights: Vec<f32>,
}

fn print_usage() {
    eprintln!(
        "Lexi FSRS-6 Parameter Optimizer Helper (v{HELPER_VERSION})\n\
         Usage:\n  \
         fsrs-optimizer                Read JSON request from stdin, write JSON response to stdout\n  \
         fsrs-optimizer --version      Print version, upstream metadata, and compatibility id\n  \
         fsrs-optimizer --probe        Print algorithm metadata, constants, and patch info JSON\n  \
         fsrs-optimizer --help         Print this help message"
    );
}

fn main() {
    let args: Vec<String> = std::env::args().collect();
    if args.len() > 1 {
        match args[1].as_str() {
            "--version" | "-v" => {
                println!(
                    "fsrs-optimizer {} (fsrs-rs {} sha:{} compat:{})",
                    HELPER_VERSION, UPSTREAM_VERSION, UPSTREAM_GIT_SHA, COMPATIBILITY_ID
                );
                process::exit(0);
            }
            "--probe" => {
                let probe = ProbeResponse {
                    helper_version: HELPER_VERSION.to_string(),
                    protocol_version: PROTOCOL_VERSION,
                    protocol: PROTOCOL_NAME.to_string(),
                    algorithm: ALGORITHM_NAME.to_string(),
                    upstream_version: UPSTREAM_VERSION.to_string(),
                    upstream_git_sha: UPSTREAM_GIT_SHA.to_string(),
                    upstream_crate_checksum: UPSTREAM_CRATE_CHECKSUM.to_string(),
                    upstream_license: UPSTREAM_LICENSE.to_string(),
                    compatibility_id: COMPATIBILITY_ID.to_string(),
                    compatibility_revision: COMPATIBILITY_ID.to_string(),
                    patch_sha256: PATCH_SHA256.to_string(),
                    s_min: 0.001,
                    default_decay: 0.1542,
                    max_sequence_length: MAX_SEQUENCE_LENGTH,
                    fixed_seed: OFFICIAL_FIXED_SEED,
                    default_weights: DEFAULT_PARAMETERS.to_vec(),
                };
                match serde_json::to_string_pretty(&probe) {
                    Ok(json) => {
                        println!("{json}");
                        process::exit(0);
                    }
                    Err(e) => {
                        eprintln!("Failed to serialize probe response: {e}");
                        process::exit(1);
                    }
                }
            }
            "--help" | "-h" => {
                print_usage();
                process::exit(0);
            }
            unknown => {
                eprintln!("Unknown argument: {unknown}");
                print_usage();
                process::exit(1);
            }
        }
    }

    // Read bounded UTF-8 JSON from stdin (reject input > MAX_INPUT_BYTES)
    let mut input_str = String::new();
    let take_reader = io::stdin().take((MAX_INPUT_BYTES + 1) as u64);
    let mut reader = take_reader;
    if let Err(e) = reader.read_to_string(&mut input_str) {
        eprintln!("Failed to read stdin: {e}");
        process::exit(1);
    }

    if input_str.len() > MAX_INPUT_BYTES {
        eprintln!(
            "Input size exceeds maximum budget of {} bytes (got >{})",
            MAX_INPUT_BYTES, MAX_INPUT_BYTES
        );
        process::exit(1);
    }

    if input_str.trim().is_empty() {
        eprintln!("Empty request received on stdin");
        process::exit(1);
    }

    let request: OptimizeRequest = match serde_json::from_str(&input_str) {
        Ok(req) => req,
        Err(e) => {
            eprintln!("Invalid JSON format or schema error: {e}");
            process::exit(1);
        }
    };

    // Protocol & field validations
    if request.version != PROTOCOL_VERSION {
        eprintln!(
            "Unsupported protocol version: expected {}, got {}",
            PROTOCOL_VERSION, request.version
        );
        process::exit(1);
    }

    if request.algorithm != ALGORITHM_NAME {
        eprintln!(
            "Unsupported algorithm: expected {}, got {}",
            ALGORITHM_NAME, request.algorithm
        );
        process::exit(1);
    }

    // Strict threads validation: CPU threads must be 1
    if request.threads != 1 {
        eprintln!(
            "Unsupported thread count: expected 1, got {}",
            request.threads
        );
        process::exit(1);
    }

    // Strict seed validation: official upstream 5.2.0 API uses fixed seed 2023
    if request.seed != OFFICIAL_FIXED_SEED {
        eprintln!(
            "Unsupported random seed: official upstream 5.2.0 API uses fixed seed {} (got {}); requests with other seeds cannot take effect",
            OFFICIAL_FIXED_SEED, request.seed
        );
        process::exit(1);
    }

    if request.items.is_empty() {
        eprintln!("Optimization requested with empty items list");
        process::exit(1);
    }

    if request.items.len() > MAX_ITEMS_BUDGET {
        eprintln!(
            "Items count {} exceeds maximum budget of {}",
            request.items.len(),
            MAX_ITEMS_BUDGET
        );
        process::exit(1);
    }

    // Configure thread count (strictly 1)
    let _ = rayon::ThreadPoolBuilder::new()
        .num_threads(1)
        .build_global();
    std::env::set_var("RAYON_NUM_THREADS", "1");

    // Outlier filtering setting
    if request.filter_outliers != Some(true) {
        std::env::set_var("FSRS_NO_OUTLIER", "1");
    } else {
        std::env::remove_var("FSRS_NO_OUTLIER");
    }

    // Validate review counts, budget, and structure without leaking raw review data
    let mut total_review_cells: usize = 0;
    let mut fsrs_items = Vec::with_capacity(request.items.len());

    for (item_idx, item) in request.items.iter().enumerate() {
        if item.reviews.is_empty() {
            eprintln!("Item at index {item_idx} has empty review list");
            process::exit(1);
        }

        if item.reviews.len() > MAX_SEQUENCE_LENGTH {
            eprintln!(
                "Item at index {item_idx} has {} reviews, exceeding maximum sequence length budget {}",
                item.reviews.len(),
                MAX_SEQUENCE_LENGTH
            );
            process::exit(1);
        }

        total_review_cells += item.reviews.len();
        if total_review_cells > MAX_TOTAL_REVIEW_CELLS {
            eprintln!(
                "Total review cells reached {}, exceeding maximum budget of {}",
                total_review_cells,
                MAX_TOTAL_REVIEW_CELLS
            );
            process::exit(1);
        }

        if item.reviews[0].delta_t != 0 {
            eprintln!(
                "Item at index {item_idx} has non-zero initial delta_t ({})",
                item.reviews[0].delta_t
            );
            process::exit(1);
        }

        let mut reviews = Vec::with_capacity(item.reviews.len());
        for (rev_idx, rev) in item.reviews.iter().enumerate() {
            if !(1..=4).contains(&rev.rating) {
                eprintln!(
                    "Item at index {item_idx}, review at index {rev_idx} has invalid rating {} (must be 1..=4)",
                    rev.rating
                );
                process::exit(1);
            }
            reviews.push(FSRSReview {
                rating: rev.rating,
                delta_t: rev.delta_t,
            });
        }

        fsrs_items.push(FSRSItem { reviews });
    }

    // Filter items with long_term_review_cnt > 0 (delta_t > 0 positive intervals)
    let training_set: Vec<FSRSItem> = fsrs_items
        .into_iter()
        .filter(|it| it.long_term_review_cnt() > 0)
        .collect();

    if training_set.is_empty() {
        eprintln!("No eligible training items with delta_t > 0 found");
        process::exit(2);
    }

    let fsrs = match FSRS::new(None) {
        Ok(f) => f,
        Err(e) => {
            eprintln!("Failed to initialize FSRS engine: {e}");
            process::exit(2);
        }
    };

    let compute_input = ComputeParametersInput {
        train_set: training_set.clone(),
        enable_short_term: request.enable_short_term,
        ..Default::default()
    };

    let optimized_weights = match fsrs.compute_parameters(compute_input) {
        Ok(weights) => weights,
        Err(e) => {
            eprintln!("Optimization computation failed: {e}");
            process::exit(2);
        }
    };

    if optimized_weights.len() != 21 {
        eprintln!(
            "Internal error: expected 21 parameters, got {}",
            optimized_weights.len()
        );
        process::exit(2);
    }

    // Verify all weights are finite and within clip bounds (allowing 1e-4 tolerance for float rounding)
    for (idx, &w) in optimized_weights.iter().enumerate() {
        if w.is_nan() || w.is_infinite() {
            eprintln!("Internal error: parameter w{idx} is non-finite ({w})");
            process::exit(2);
        }
        let (lower, upper) = OFFICIAL_CLIP_BOUNDS[idx];
        if w < lower - 1e-4 || w > upper + 1e-4 {
            eprintln!(
                "Internal error: parameter w{idx} = {w} outside official clip bounds [{lower}, {upper}] beyond tolerance"
            );
            process::exit(2);
        }
    }

    // Check if parameters evolved from defaults.
    // CONTRACT RULE: "错误非零退出，不能返回 defaults 冒充训练。"
    let evolved_count = optimized_weights
        .iter()
        .zip(DEFAULT_PARAMETERS.iter())
        .filter(|(&opt, &def)| (opt - def).abs() > 1e-5)
        .count();

    if evolved_count == 0 {
        eprintln!(
            "Training did not evolve parameters beyond defaults (insufficient data or zero gradients). Refusing to return defaults as trained weights."
        );
        process::exit(3);
    }

    let response = OptimizeResponse {
        version: PROTOCOL_VERSION,
        algorithm: ALGORITHM_NAME.to_string(),
        protocol: PROTOCOL_NAME.to_string(),
        model_version: UPSTREAM_VERSION.to_string(),
        git_sha: UPSTREAM_GIT_SHA.to_string(),
        parameter_count: 21,
        weights: optimized_weights,
        effective_seed: OFFICIAL_FIXED_SEED,
        items_count: training_set.len(),
        evolved_count,
        max_sequence_length: MAX_SEQUENCE_LENGTH,
        compatibility_id: COMPATIBILITY_ID.to_string(),
        compatibility_revision: COMPATIBILITY_ID.to_string(),
        patch_sha256: PATCH_SHA256.to_string(),
    };

    let response_json = match serde_json::to_string(&response) {
        Ok(j) => j,
        Err(e) => {
            eprintln!("Failed to serialize response JSON: {e}");
            process::exit(2);
        }
    };

    // Output ONLY the single JSON response to stdout
    let stdout = io::stdout();
    let mut handle = stdout.lock();
    if let Err(e) = writeln!(handle, "{response_json}") {
        eprintln!("Failed to write response to stdout: {e}");
        process::exit(2);
    }
}
