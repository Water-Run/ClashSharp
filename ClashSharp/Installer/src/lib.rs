#![doc(test(attr(deny(warnings))))]

//! Shared installer helpers.
//!
//! The binary target owns UI wiring and package actions. Pure parsing helpers
//! live here so Rustdoc examples and unit tests can exercise them directly.

pub mod installer_transaction;
pub mod metadata;
pub mod service_plan;
pub mod trust_anchor;
