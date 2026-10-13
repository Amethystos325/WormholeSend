//! Where a received file goes inside the save directory.
//!
//! Protocol v2 file names may carry folder components (`Photos/2026/a.jpg`)
//! for folder transfers. Unlike `localsend::util::filename::sanitize_path`,
//! which keeps only the last segment, the folder structure is preserved here:
//! every segment is sanitized on its own and `.`/`..` segments are dropped, so
//! a peer can never write outside the save directory.

use localsend::util::filename::{self, Rules};
use std::collections::HashMap;
use std::path::{Path, PathBuf};

/// Returns a path for `file_name` inside `dir` that does not exist yet.
///
/// A single file that collides is renamed to `name (1).ext`. A folder whose
/// top-level name collides is renamed once per transfer to `name (1)`, and
/// every file of the transfer under that folder follows the rename, tracked
/// in `roots`.
pub fn receive_path(dir: &Path, file_name: &str, roots: &mut HashMap<String, String>) -> PathBuf {
    let rules = Rules::current();
    let segments: Vec<String> = file_name
        .split(['/', '\\'])
        .filter(|segment| !segment.is_empty() && *segment != "." && *segment != "..")
        .map(|segment| filename::sanitize(segment, rules))
        .filter(|segment| !segment.is_empty() && segment != "." && segment != "..")
        .collect();

    match segments.as_slice() {
        [] => unique_file(dir, "unnamed"),
        [name] => unique_file(dir, name),
        [root, rest @ ..] => {
            let root = roots
                .entry(root.clone())
                .or_insert_with(|| unique_dir_name(dir, root))
                .clone();
            let mut path = dir.join(root);
            let (last, folders) = rest.split_last().expect("rest is not empty");
            for folder in folders {
                path.push(folder);
            }
            unique_file(&path, last)
        }
    }
}

fn unique_file(dir: &Path, name: &str) -> PathBuf {
    let candidate = dir.join(name);
    if !candidate.exists() {
        return candidate;
    }
    let (stem, extension) = match name.rsplit_once('.') {
        Some((stem, extension)) if !stem.is_empty() => (stem, format!(".{extension}")),
        _ => (name, String::new()),
    };
    (1..)
        .map(|i| dir.join(format!("{stem} ({i}){extension}")))
        .find(|candidate| !candidate.exists())
        .expect("an unused name exists")
}

fn unique_dir_name(dir: &Path, name: &str) -> String {
    if !dir.join(name).exists() {
        return name.to_string();
    }
    (1..)
        .map(|i| format!("{name} ({i})"))
        .find(|candidate| !dir.join(candidate).exists())
        .expect("an unused name exists")
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn keeps_folders_and_stays_inside_dir() {
        let dir = tempfile::tempdir().unwrap();
        let mut roots = HashMap::new();
        let path = receive_path(dir.path(), "Photos/2026/a.jpg", &mut roots);
        assert_eq!(path, dir.path().join("Photos").join("2026").join("a.jpg"));

        let path = receive_path(dir.path(), "../../etc/passwd", &mut roots);
        assert_eq!(path, dir.path().join("etc").join("passwd"));

        let path = receive_path(dir.path(), "/abs/../x.txt", &mut roots);
        assert!(path.starts_with(dir.path()));
    }

    #[test]
    fn renames_colliding_files_and_folders() {
        let dir = tempfile::tempdir().unwrap();
        std::fs::write(dir.path().join("a.txt"), "x").unwrap();
        std::fs::create_dir(dir.path().join("Photos")).unwrap();

        let mut roots = HashMap::new();
        assert_eq!(receive_path(dir.path(), "a.txt", &mut roots), dir.path().join("a (1).txt"));
        let first = receive_path(dir.path(), "Photos/a.jpg", &mut roots);
        let second = receive_path(dir.path(), "Photos/b.jpg", &mut roots);
        assert_eq!(first, dir.path().join("Photos (1)").join("a.jpg"));
        assert_eq!(second, dir.path().join("Photos (1)").join("b.jpg"));
    }
}
