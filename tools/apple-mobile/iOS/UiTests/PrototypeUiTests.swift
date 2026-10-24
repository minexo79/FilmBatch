import XCTest

final class PrototypeUiTests: XCTestCase {
    private var app: XCUIApplication!

    override func setUpWithError() throws {
        continueAfterFailure = false
        app = XCUIApplication(bundleIdentifier: ProcessInfo.processInfo.environment["FILMBATCH_BUNDLE_ID_PROTOTYPE"] ?? "org.filmbatch.mobile.prototype")
    }

    private func launch(large: Bool = false) {
        if app.state != .notRunning { app.terminate() }
        app.launchArguments = large ? ["-probe-width", "8000", "-probe-height", "6000", "-probe-no-gpu"] : []
        app.launch()
        XCTAssertTrue(app.buttons["Prototype.Run"].waitForExistence(timeout: 30))
        XCTAssertTrue(app.waitForEnabled("Prototype.Run", timeout: 30))
    }

    private var preview: XCUIElement {
        app.descendants(matching: .any).matching(identifier: "Prototype.Preview").firstMatch
    }

    func testRunCompletesWithReportAndPreview() throws {
        launch()
        app.buttons["Prototype.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Prototype", run: 1))
        try app.verifyNativeReport("Prototype")
        XCTAssertEqual(preview.label, "ImageIO output preview 51x37", "the real ImageIO bitmap must be delivered, not just an empty Image control")
        let image = XCTAttachment(screenshot: app.screenshot())
        image.lifetime = .keepAlways
        add(image)
    }

    func testCancelDiscardsResultAndAllowsFreshRun() throws {
        launch(large: true)
        app.buttons["Prototype.Run"].tap()
        XCTAssertTrue(app.waitForEnabled("Prototype.Cancel", timeout: 10))
        app.buttons["Prototype.Cancel"].tap()
        XCTAssertTrue(app.waitForStatus("Prototype", containing: "discarded"))
        XCTAssertTrue(app.waitForEnabled("Prototype.Run", timeout: 60))
        XCTAssertFalse(app.buttons["Prototype.Cancel"].isEnabled)
        XCTAssertEqual(app.probeReportText("Prototype"), "")
        XCTAssertEqual(preview.label, "ImageIO output preview", "cancelled work must not deliver a stale bitmap")
        app.buttons["Prototype.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Prototype", run: 2))
        try app.verifyNativeReport("Prototype")
        XCTAssertEqual(preview.label, "ImageIO output preview 51x37")
    }

    func testBackgroundCancelsAndForegroundAllowsRetry() throws {
        launch(large: true)
        app.buttons["Prototype.Run"].tap()
        XCTAssertTrue(app.waitForEnabled("Prototype.Cancel", timeout: 10))
        app.backgroundForCancellation()
        app.activate()
        XCTAssertTrue(app.waitForStatus("Prototype", containing: "discarded"))
        XCTAssertTrue(app.waitForEnabled("Prototype.Run", timeout: 60))
        XCTAssertEqual(app.probeReportText("Prototype"), "")
        XCTAssertEqual(preview.label, "ImageIO output preview")
        app.buttons["Prototype.Run"].tap()
        XCTAssertTrue(app.waitForCompletion("Prototype", run: 2))
        try app.verifyNativeReport("Prototype")
    }
}
