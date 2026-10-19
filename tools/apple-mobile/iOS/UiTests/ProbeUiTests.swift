import XCTest

// Real controls and native reports; a running label is never accepted as completion.
final class ProbeUiTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: ProcessInfo.processInfo.environment["FILMBATCH_BUNDLE_ID"] ?? "org.filmbatch.mobile.probe")
    }

    private func launch(large: Bool = false) {
        if app.state != .notRunning { app.terminate() }
        app.launchArguments = large ? ["-probe-width", "8000", "-probe-height", "6000", "-probe-no-gpu"] : []
        app.launch()
        XCTAssertTrue(app.buttons["Probe.Run"].waitForExistence(timeout: 30))
        XCTAssertTrue(app.waitForEnabled("Probe.Run", timeout: 30))
    }

    func testRunCompletesWithRealReport() throws {
        launch()
        app.buttons["Probe.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Probe", run: 1))
        try app.verifyNativeReport("Probe")
        let image = XCTAttachment(screenshot: app.screenshot())
        image.lifetime = .keepAlways
        add(image)
    }

    func testCancelDiscardsResultAndRerunDeliversNewResult() throws {
        launch(large: true)
        app.buttons["Probe.Run"].tap()
        XCTAssertTrue(app.waitForEnabled("Probe.Cancel", timeout: 10))
        app.buttons["Probe.Cancel"].tap()
        XCTAssertTrue(app.waitForStatus("Probe", containing: "discarded"))
        XCTAssertTrue(app.waitForEnabled("Probe.Run", timeout: 60))
        XCTAssertFalse(app.buttons["Probe.Cancel"].isEnabled)
        XCTAssertEqual(app.probeReportText("Probe"), "", "cancelled work must not publish a visible report")
        app.buttons["Probe.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Probe", run: 2))
        try app.verifyNativeReport("Probe")
    }

    func testBackgroundCancelsAndForegroundAllowsRetry() throws {
        launch(large: true)
        app.buttons["Probe.Run"].tap()
        XCTAssertTrue(app.waitForEnabled("Probe.Cancel", timeout: 10))
        app.backgroundForCancellation()
        app.activate()
        XCTAssertTrue(app.waitForStatus("Probe", containing: "discarded"))
        XCTAssertTrue(app.waitForEnabled("Probe.Run", timeout: 60))
        XCTAssertEqual(app.probeReportText("Probe"), "")
        app.buttons["Probe.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Probe", run: 2))
        try app.verifyNativeReport("Probe")
    }
}

extension XCUIApplication {
    func backgroundForCancellation() {
        // Activate Home directly; device-wide HID can target the other Duo display.
        XCUIApplication(bundleIdentifier: "com.apple.springboard").activate()
        let background = NSPredicate { _, _ in
            self.state == .runningBackground || self.state == .runningBackgroundSuspended
        }
        let entered = XCTNSPredicateExpectation(predicate: background, object: self)
        XCTAssertEqual(XCTWaiter.wait(for: [entered], timeout: 10), .completed,
                       "the tested app must actually enter the background; state=\(state.rawValue)")
    }

    func waitForEnabled(_ identifier: String, timeout: TimeInterval) -> Bool {
        let check = XCTNSPredicateExpectation(predicate: NSPredicate(format: "enabled == true"), object: buttons[identifier])
        return XCTWaiter.wait(for: [check], timeout: timeout) == .completed
    }

    func waitForStatus(_ prefix: String, containing text: String) -> Bool {
        let status = descendants(matching: .any).matching(identifier: prefix + ".Status").firstMatch
        let check = XCTNSPredicateExpectation(predicate: NSPredicate(format: "label CONTAINS %@", text), object: status)
        return XCTWaiter.wait(for: [check], timeout: 60) == .completed
    }

    func waitForCompletion(_ prefix: String, run: Int) -> Bool {
        let status = descendants(matching: .any).matching(identifier: prefix + ".Status").firstMatch
        let check = XCTNSPredicateExpectation(predicate: NSPredicate(format: "label BEGINSWITH %@", "Run \(run) complete;"), object: status)
        let completed = XCTWaiter.wait(for: [check], timeout: 240) == .completed
        if completed {
            XCTAssertTrue(buttons[prefix + ".Run"].isEnabled)
            XCTAssertFalse(buttons[prefix + ".Cancel"].isEnabled)
        }
        return completed
    }

    func probeReportText(_ prefix: String) -> String {
        let report = descendants(matching: .any).matching(identifier: prefix + ".Report").firstMatch
        return report.value as? String ?? report.label
    }

    func verifyNativeReport(_ prefix: String) throws {
        let report = try XCTUnwrap(JSONSerialization.jsonObject(with: Data(probeReportText(prefix).utf8)) as? [String: Any])
        XCTAssertEqual(report["OperatingSystem"] as? String, "iOS", "a Mac report is not iOS evidence")
        let results = try XCTUnwrap(report["Results"] as? [[String: Any]])
        for name in ["core-raw", "apple-imageio", "onnx"] {
            let result = try XCTUnwrap(results.first { $0["Name"] as? String == name })
            XCTAssertEqual(result["Status"] as? String, "pass", "\(name) must execute successfully: \(result)")
        }
        let gpu = try XCTUnwrap(results.first { $0["Name"] as? String == "gpu" })
        XCTAssertTrue(["pass", "blocked", "skipped"].contains(gpu["Status"] as? String ?? ""), "missing iOS Dawn must remain explicit: \(gpu)")
    }
}
