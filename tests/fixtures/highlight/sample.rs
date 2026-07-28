// nib highlight fixture
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct Config {
    pub entries: HashMap<String, String>,
}

impl Config {
    pub fn get(&self, key: &str) -> Option<&String> {
        self.entries.get(key)
    }
}
