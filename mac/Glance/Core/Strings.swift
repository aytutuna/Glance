import Foundation

private final class Anchor {}

func L(_ key: String, _ args: Any...) -> String {
    let format = Bundle(for: Anchor.self).localizedString(forKey: key, value: key, table: nil)
    return String(format: format, arguments: args.map { String(describing: $0) })
}
