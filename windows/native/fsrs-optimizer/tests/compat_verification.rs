use fsrs::{FSRSItem, FSRSReview, FSRS, DEFAULT_PARAMETERS};

// C# Fsrs6Weights.OfficialDefaults reference values
const CSHARP_DEFAULTS: [f32; 21] = [
    0.212, 1.2931, 2.3065, 8.2956, 6.4133, 0.8334, 3.0194, 0.001,
    1.8722, 0.1666, 0.796, 1.4835, 0.0614, 0.2629, 1.6483, 0.6014,
    1.8729, 0.5425, 0.0912, 0.0658, 0.1542,
];

#[test]
fn test_default_weights_against_csharp_defaults() {
    let diff = DEFAULT_PARAMETERS
        .iter()
        .zip(CSHARP_DEFAULTS.iter())
        .map(|(a, b)| (a - b).abs())
        .fold(0.0f32, f32::max);
    assert!(
        diff < 1e-6,
        "Max difference between Rust DEFAULT_PARAMETERS and C# defaults is {diff} (must be < 1e-6)"
    );
}

#[test]
fn test_direct_patched_memory_state_same_day_hard_floor() {
    let fsrs = FSRS::new(Some(&DEFAULT_PARAMETERS)).expect("FSRS init failed");

    // 1. Initial Good review (delta_t = 0)
    let initial = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![FSRSReview {
                    rating: 3,
                    delta_t: 0,
                }],
            },
            None,
        )
        .expect("memory_state initial failed");

    println!(
        "Patched FSRS 5.2.0: Good0 initial S={} D={}",
        initial.stability, initial.difficulty
    );
    assert!(
        (initial.stability - 2.3065).abs() < 1e-4,
        "Initial Good0 stability must be 2.3065, got {}",
        initial.stability
    );

    // 2. Same-day Hard review (Good0 -> Hard0)
    let after_hard = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                    FSRSReview {
                        rating: 2,
                        delta_t: 0,
                    },
                ],
            },
            None,
        )
        .expect("memory_state same-day Hard failed");

    println!(
        "Patched FSRS 5.2.0: Good0 -> Hard0 same-day S={} D={}",
        after_hard.stability, after_hard.difficulty
    );

    // CRITICAL: Under unpatched 5.2.0, stability dropped to 1.3333788 (exit 134).
    // Under patched model, rating >= 2 has floor sinc.clamp_min(1.0), so S must NOT drop below initial S!
    assert!(
        after_hard.stability >= initial.stability - 1e-5,
        "Same-day Hard stability ({}) dropped below initial stability ({})!",
        after_hard.stability,
        initial.stability
    );
    assert!(
        (after_hard.stability - 2.3065).abs() < 1e-4,
        "Same-day Hard stability expected 2.3065, got {}",
        after_hard.stability
    );

    // 3. Same-day Again review (Good0 -> Again0): MUST drop stability
    let after_again = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                    FSRSReview {
                        rating: 1,
                        delta_t: 0,
                    },
                ],
            },
            None,
        )
        .expect("memory_state same-day Again failed");

    println!(
        "Patched FSRS 5.2.0: Good0 -> Again0 same-day S={} D={}",
        after_again.stability, after_again.difficulty
    );
    assert!(
        after_again.stability < initial.stability,
        "Same-day Again stability ({}) must drop below initial stability ({})",
        after_again.stability,
        initial.stability
    );

    // 4. Same-day Good review (Good0 -> Good0): stability must be >= initial
    let after_good = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                ],
            },
            None,
        )
        .expect("memory_state same-day Good failed");

    println!(
        "Patched FSRS 5.2.0: Good0 -> Good0 same-day S={} D={}",
        after_good.stability, after_good.difficulty
    );
    assert!(
        after_good.stability >= initial.stability,
        "Same-day Good stability ({}) must be >= initial ({})",
        after_good.stability,
        initial.stability
    );

    // 5. Same-day Easy review (Good0 -> Easy0): stability must be >= Good
    let after_easy = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                    FSRSReview {
                        rating: 4,
                        delta_t: 0,
                    },
                ],
            },
            None,
        )
        .expect("memory_state same-day Easy failed");

    println!(
        "Patched FSRS 5.2.0: Good0 -> Easy0 same-day S={} D={}",
        after_easy.stability, after_easy.difficulty
    );
    assert!(
        after_easy.stability >= after_good.stability,
        "Same-day Easy stability ({}) must be >= Good ({})",
        after_easy.stability,
        after_good.stability
    );

    // 6. Long-term review (delta_t = 3 days, Good)
    let after_long_term = fsrs
        .memory_state(
            FSRSItem {
                reviews: vec![
                    FSRSReview {
                        rating: 3,
                        delta_t: 0,
                    },
                    FSRSReview {
                        rating: 3,
                        delta_t: 3,
                    },
                ],
            },
            None,
        )
        .expect("memory_state long-term failed");

    println!(
        "Patched FSRS 5.2.0: Good0 -> Good(delta_t=3) long-term S={} D={}",
        after_long_term.stability, after_long_term.difficulty
    );
    assert!(
        after_long_term.stability > initial.stability,
        "Long-term Good review must increase stability over initial"
    );
}

#[test]
fn test_reference_vector_against_csharp_formulas() {
    let fsrs = FSRS::new(Some(&DEFAULT_PARAMETERS)).expect("FSRS init failed");

    // Test a matrix of initial stabilities across ratings
    let test_seeds = [0.212f32, 1.2931, 2.3065, 8.2956];
    for &seed_s in &test_seeds {
        // C# ShortTermStability formula for reference comparison:
        // increase = exp(w17 * (rating - 3 + w18)) * s^(-w19)
        // if rating >= 2: increase = max(increase, 1.0)
        let w17 = DEFAULT_PARAMETERS[17];
        let w18 = DEFAULT_PARAMETERS[18];
        let w19 = DEFAULT_PARAMETERS[19];

        for rating in 1..=4 {
            let sinc = (w17 * (rating as f32 - 3.0 + w18)).exp() * seed_s.powf(-w19);
            let expected_sinc = if rating >= 2 { sinc.max(1.0) } else { sinc };
            let expected_s = (seed_s * expected_sinc).clamp(0.001, 36500.0);

            // Compute via direct Rust FSRS memory_state starting from custom starting state
            let rust_state = fsrs
                .memory_state(
                    FSRSItem {
                        reviews: vec![
                            FSRSReview {
                                rating,
                                delta_t: 0,
                            },
                        ],
                    },
                    Some(fsrs::MemoryState {
                        stability: seed_s,
                        difficulty: 5.0,
                    }),
                )
                .expect("memory_state failed");

            let diff = (rust_state.stability - expected_s).abs();
            assert!(
                diff < 1e-4,
                "Mismatch for S={seed_s}, rating={rating}: Rust={}, Expected C#={expected_s}, diff={diff}",
                rust_state.stability
            );
        }
    }
}

#[test]
fn test_candidate_weights_reference_vector() {
    let candidate_weights: [f32; 21] = [
        0.14760943, 11.232108, 17.288622, 30.781494, 6.2962017, 1.0365819, 2.9175336, 0.16811314,
        2.1309464, 0.11510812, 1.038854, 1.5383226, 0.036133267, 0.33423546, 1.7174095, 0.6351958,
        2.150446, 0.55714285, 0.10585633, 0.11753233, 0.1,
    ];
    let fsrs = FSRS::new(Some(&candidate_weights)).expect("FSRS init with candidate weights failed");

    let test_seeds = [0.1476f32, 1.5, 5.0, 20.0];
    let w17 = candidate_weights[17];
    let w18 = candidate_weights[18];
    let w19 = candidate_weights[19];

    for &seed_s in &test_seeds {
        for rating in 1..=4 {
            let sinc = (w17 * (rating as f32 - 3.0 + w18)).exp() * seed_s.powf(-w19);
            let expected_sinc = if rating >= 2 { sinc.max(1.0) } else { sinc };
            let expected_s = (seed_s * expected_sinc).clamp(0.001, 36500.0);

            let rust_state = fsrs
                .memory_state(
                    FSRSItem {
                        reviews: vec![FSRSReview { rating, delta_t: 0 }],
                    },
                    Some(fsrs::MemoryState {
                        stability: seed_s,
                        difficulty: 5.0,
                    }),
                )
                .expect("memory_state failed");

            let diff = (rust_state.stability - expected_s).abs();
            assert!(
                diff < 1e-4,
                "Candidate mismatch for S={seed_s}, rating={rating}: Rust={}, Expected C#={expected_s}, diff={diff}",
                rust_state.stability
            );
        }
    }
}

