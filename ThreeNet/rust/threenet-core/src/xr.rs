//! OpenXR discovery. The loader is opened dynamically, so machines without an
//! OpenXR runtime simply report "not available". [`probe`] creates a headless
//! instance and queries the head mounted system: runtime, headset name and the
//! recommended per-eye resolution. Stereo rendering itself uses off-axis
//! cameras ([`crate::camera::Projection::OffAxis`]); submitting frames to a
//! headset swapchain is not implemented yet.

#[derive(Debug, Clone, Default, PartialEq)]
pub struct XrProbe {
    /// The OpenXR loader library could be opened.
    pub loader_found: bool,
    /// A runtime is installed and an instance could be created.
    pub runtime_found: bool,
    /// A head mounted display is connected.
    pub headset_found: bool,
    pub runtime_name: String,
    pub runtime_version: String,
    pub system_name: String,
    pub vendor_id: u32,
    pub recommended_width: u32,
    pub recommended_height: u32,
    pub view_count: u32,
    pub orientation_tracking: bool,
    pub position_tracking: bool,
    /// Why probing stopped, if it did.
    pub message: String,
}

#[cfg(not(feature = "xr"))]
pub fn probe() -> XrProbe {
    XrProbe { message: "OpenXR support is not included in this build".into(), ..Default::default() }
}

#[cfg(feature = "xr")]
pub fn probe() -> XrProbe {
    let mut result = XrProbe::default();
    // SAFETY: loading the system OpenXR loader; failure is reported, not fatal.
    let entry = match unsafe { openxr::Entry::load() } {
        Ok(entry) => entry,
        Err(error) => {
            result.message = format!("OpenXR loader not found: {error}");
            return result;
        }
    };
    result.loader_found = true;

    let app = openxr::ApplicationInfo {
        application_name: "Three.Net probe",
        application_version: 1,
        engine_name: "Three.Net",
        engine_version: 1,
        api_version: openxr::Version::new(1, 0, 0),
    };
    let instance = match entry.create_instance(&app, &openxr::ExtensionSet::default(), &[]) {
        Ok(instance) => instance,
        Err(error) => {
            result.message = format!("no OpenXR runtime: {error}");
            return result;
        }
    };
    result.runtime_found = true;
    if let Ok(properties) = instance.properties() {
        result.runtime_name = properties.runtime_name;
        let v = properties.runtime_version;
        result.runtime_version = format!("{}.{}.{}", v.major(), v.minor(), v.patch());
    }

    let system = match instance.system(openxr::FormFactor::HEAD_MOUNTED_DISPLAY) {
        Ok(system) => system,
        Err(error) => {
            result.message = format!("no headset: {error}");
            return result;
        }
    };
    result.headset_found = true;
    if let Ok(properties) = instance.system_properties(system) {
        result.system_name = properties.system_name;
        result.vendor_id = properties.vendor_id;
        result.orientation_tracking = properties.tracking_properties.orientation_tracking;
        result.position_tracking = properties.tracking_properties.position_tracking;
    }
    if let Ok(views) = instance.enumerate_view_configuration_views(system, openxr::ViewConfigurationType::PRIMARY_STEREO) {
        result.view_count = views.len() as u32;
        if let Some(first) = views.first() {
            result.recommended_width = first.recommended_image_rect_width;
            result.recommended_height = first.recommended_image_rect_height;
        }
    }
    result
}

#[cfg(test)]
mod tests {
    #[test]
    fn probing_never_panics() {
        let probe = super::probe();
        // Without a runtime the message explains why; with one, the name is set.
        assert!(probe.runtime_found || !probe.message.is_empty());
        if probe.headset_found {
            assert!(probe.view_count >= 1);
        }
    }
}
